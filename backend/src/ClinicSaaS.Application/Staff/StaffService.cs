using System.Net;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Auth;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Application.Staff;

public sealed record RoleRef(Guid Id, string Name);

public sealed record StaffMemberDto(
    Guid MembershipId, Guid UserId, string FullName, string Email, string? PhoneNumber, MembershipStatus Status, bool IsOwner,
    bool EmailConfirmed, bool HasPassword, IReadOnlyList<RoleRef> Roles, IReadOnlyList<Guid> BranchIds, Guid? DoctorId,
    DateTime? LastLoginAtUtc, string RowVersion);

public sealed record StaffListQuery : PageQuery
{
    public MembershipStatus? Status { get; init; }
    public Guid? RoleId { get; init; }
}

public sealed record InviteStaffRequest(string FullName, string Email, string? PhoneNumber, IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid>? BranchIds);

public sealed record UpdateStaffRequest(IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid>? BranchIds, string RowVersion);

public sealed class InviteStaffValidator : AbstractValidator<InviteStaffRequest>
{
    public InviteStaffValidator()
    {
        RuleFor(x => x.FullName).RequiredText().MaxLen(200);
        RuleFor(x => x.Email).RequiredText().ValidEmail();
        RuleFor(x => x.PhoneNumber).SaudiMobile();
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage(ValidationCodes.Required);
    }
}

public sealed class UpdateStaffValidator : AbstractValidator<UpdateStaffRequest>
{
    public UpdateStaffValidator()
    {
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

/// <summary>
/// Clinic staff = memberships. Every new seat is a pending invitation (status Invited) bound to a one-time link:
/// new people set their password with it, people who already have an account (e.g. a doctor at another clinic) sign
/// in and accept it. No access, and no view of the account's profile, before acceptance. The owner seat is never editable here,
/// nobody can change their own seat, and assigned roles can't exceed the actor's own permissions.
/// </summary>
public sealed class StaffService(
    IAppDbContext db,
    ICurrentUser actor,
    ClinicContext clinic,
    IUsageLimiter limits,
    ITokenService tokens,
    IEmailSender email,
    IClock clock,
    IAuditLogger audit,
    IRequestContext request,
    IOptions<AuthOptions> authOptions,
    IValidator<InviteStaffRequest> inviteValidator,
    IValidator<UpdateStaffRequest> updateValidator,
    ILogger<StaffService> logger)
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public async Task<PagedResult<StaffMemberDto>> ListAsync(StaffListQuery query, CancellationToken ct)
    {
        var q = db.TenantMemberships.AsNoTracking();
        if (query.Status is { } s) q = q.Where(m => m.Status == s);
        if (query.RoleId is { } r) q = q.Where(m => m.Roles.Any(x => x.RoleId == r));
        // A seat whose account never joined this clinic (pending, or disabled before acceptance) shows nothing of the
        // account: it is matched by email only and sorted by email, so neither search nor order reveals the name (QA-02, QA-12).
        var joined = JoinedUserIds();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(m => ((m.Status == MembershipStatus.Active || (m.Status == MembershipStatus.Disabled && joined.Contains(m.UserId)))
                              && m.User!.FullName.StartsWith(term))
                             || m.User!.Email.StartsWith(term));
        }

        var total = await q.CountAsync(ct);
        var page = await q.OrderByDescending(m => m.IsOwner)
            .ThenBy(m => m.Status == MembershipStatus.Active || (m.Status == MembershipStatus.Disabled && joined.Contains(m.UserId))
                ? m.User!.FullName : m.User!.NormalizedEmail)
            .Skip((query.SafePage - 1) * query.SafePageSize).Take(query.SafePageSize)
            .Include(m => m.User).Include(m => m.Roles).ThenInclude(r => r.Role).Include(m => m.Branches)
            .ToListAsync(ct);
        var doctors = await DoctorIdsAsync(page.Select(m => m.Id), ct);
        var formerMembers = await FormerMembersAsync(page, ct);
        return new PagedResult<StaffMemberDto>(page.Select(m => ToDto(m, doctors, formerMembers)).ToList(), query.SafePage, query.SafePageSize, total);
    }

    public async Task<StaffMemberDto> GetAsync(Guid membershipId, CancellationToken ct)
    {
        var m = await LoadAsync(membershipId, tracked: false, ct);
        return ToDto(m, await DoctorIdsAsync([m.Id], ct), await FormerMembersAsync([m], ct));
    }

    public async Task<StaffMemberDto> InviteAsync(InviteStaffRequest req, CancellationToken ct)
    {
        await inviteValidator.ValidateOrThrowAsync(req, ct);
        await limits.EnsureCanAddAsync(LimitKeys.MaxUsers, ct);
        var roles = await LoadAssignableRolesAsync(req.RoleIds, ct);
        var branchIds = await ValidateBranchesAsync(req.BranchIds, ct);
        EnsureAssignableBranches(branchIds);
        var tenant = await clinic.GetTenantAsync(ct);
        var normalized = User.Normalize(req.Email);

        // QA-02: the seat is always a pending invitation, for a new or an existing account alike, and the response is
        // the same masked view either way — nothing tells the clinic whether the email is registered.
        var (membership, invitationToken, user, existingAccount) = await db.InTransactionAsync(async c =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, c);
            var existing = user is not null && !string.IsNullOrEmpty(user.PasswordHash);
            if (user is null)
            {
                user = new User
                {
                    Email = req.Email.Trim(),
                    NormalizedEmail = normalized,
                    FullName = req.FullName.Trim(),
                    PhoneNumber = SaudiFormats.NormalizeMobile(req.PhoneNumber),
                };
                db.Users.Add(user);
            }
            else if (await db.TenantMemberships.AnyAsync(m => m.UserId == user.Id, c))
            {
                // Only reachable for someone already on THIS clinic's team, which the clinic can see anyway.
                throw new ConflictException(ErrorCodes.StaffAlreadyMember);
            }

            var m = new TenantMembership { UserId = user.Id, User = user, Status = MembershipStatus.Invited };
            foreach (var role in roles) m.Roles.Add(new MembershipRole { RoleId = role.Id, Role = role });
            foreach (var b in branchIds) m.Branches.Add(new MembershipBranch { BranchId = b });
            db.TenantMemberships.Add(m);
            var token = IssueInvitation(user, m.Id);
            audit.Add(AuditActions.StaffInvited, nameof(TenantMembership), m.Id.ToString(),
                details: new { roles = roles.Select(r => r.SystemKey ?? r.Id.ToString()).ToList() });
            await db.SaveChangesAsync(c);
            return (m, token, user, existing);
        }, ct: ct);

        await SendInvitationEmailAsync(user, tenant, invitationToken, existingAccount, ct);
        return ToDto(membership, new Dictionary<Guid, Guid>(), new HashSet<Guid>());
    }

    public async Task<StaffMemberDto> UpdateAsync(Guid membershipId, UpdateStaffRequest req, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(req, ct);
        var m = await LoadAsync(membershipId, tracked: true, ct);
        EnsureEditable(m);
        EnsureMemberWithinActorBranches(m);
        db.SetOriginalRowVersion(m, RowVersions.Decode(req.RowVersion));

        // Removing a role the actor couldn't grant is also an escalation path (e.g. stripping a manager).
        var currentRoleIds = m.Roles.Select(r => r.RoleId).ToHashSet();
        var removed = m.Roles.Where(r => !req.RoleIds.Contains(r.RoleId)).ToList();
        EnsureWithinActorPermissions(removed.Select(r => r.Role!));
        var newRoles = await LoadAssignableRolesAsync(req.RoleIds.Where(id => !currentRoleIds.Contains(id)).ToList(), ct, allowEmpty: true);
        var branchIds = await ValidateBranchesAsync(req.BranchIds, ct);
        EnsureAssignableBranches(branchIds);
        // QA-04: moving a member between branches (or lifting/adding a restriction) is as strong as disabling them, so
        // it needs the same guard: the actor must hold every permission of the member's current roles.
        if (!m.Branches.Select(b => b.BranchId).ToHashSet().SetEquals(branchIds))
            EnsureWithinActorPermissions(m.Roles.Select(r => r.Role!));

        foreach (var r in removed) m.Roles.Remove(r);
        foreach (var r in newRoles) m.Roles.Add(new MembershipRole { RoleId = r.Id, Role = r });
        foreach (var b in m.Branches.Where(b => !branchIds.Contains(b.BranchId)).ToList()) m.Branches.Remove(b);
        foreach (var b in branchIds.Where(id => m.Branches.All(x => x.BranchId != id))) m.Branches.Add(new MembershipBranch { BranchId = b });
        m.UpdatedAtUtc = clock.UtcNow;

        audit.Add(AuditActions.StaffUpdated, nameof(TenantMembership), m.Id.ToString(),
            details: new { addedRoles = newRoles.Count, removedRoles = removed.Count, branches = branchIds.Count });
        await db.SaveChangesAsync(ct);
        return ToDto(m, await DoctorIdsAsync([m.Id], ct), await FormerMembersAsync([m], ct));
    }

    public async Task SetStatusAsync(Guid membershipId, bool enabled, CancellationToken ct)
    {
        var m = await LoadAsync(membershipId, tracked: true, ct);
        EnsureEditable(m);
        EnsureMemberWithinActorBranches(m);
        EnsureWithinActorPermissions(m.Roles.Select(r => r.Role!));
        // A pending seat is "enabled" already in the sense that the invitation is open; enabling never skips acceptance.
        if (enabled ? m.Status != MembershipStatus.Disabled : m.Status == MembershipStatus.Disabled) return;

        string? token = null;
        if (enabled)
        {
            // QA-02: re-linking a disabled seat needs the account owner's consent again — it becomes a new pending
            // invitation (the schema does not record whether the seat was ever accepted, so this applies to all;
            // see JoinedUserIds for how the staff views tell a former member from a never-accepted seat).
            await limits.EnsureCanAddAsync(LimitKeys.MaxUsers, ct);
            m.Status = MembershipStatus.Invited;
            await RevokeOldInvitationLinksAsync(m, ct);
            token = IssueInvitation(m.User!, m.Id);
            audit.Add(AuditActions.StaffEnabled, nameof(TenantMembership), m.Id.ToString());
        }
        else
        {
            if (m.Status == MembershipStatus.Invited) await RevokeOldInvitationLinksAsync(m, ct);
            m.Status = MembershipStatus.Disabled;
            // End this person's sessions in this clinic immediately (access tokens expire within minutes).
            var now = clock.UtcNow;
            var tenantId = clinic.TenantId;
            await db.RefreshTokens.Where(t => t.UserId == m.UserId && t.TenantId == tenantId && t.RevokedAtUtc == null)
                .ForEachAsync(t => t.Revoke(now, "membership_disabled"), ct);
            audit.Add(AuditActions.StaffDisabled, nameof(TenantMembership), m.Id.ToString());
        }
        await db.SaveChangesAsync(ct);
        if (token is not null)
            await SendInvitationEmailAsync(m.User!, await clinic.GetTenantAsync(ct), token, !string.IsNullOrEmpty(m.User!.PasswordHash), ct);
    }

    public async Task ResendInvitationAsync(Guid membershipId, CancellationToken ct)
    {
        var m = await LoadAsync(membershipId, tracked: true, ct);
        var legacyPending = m.Status == MembershipStatus.Active && string.IsNullOrEmpty(m.User!.PasswordHash);
        if (m.Status != MembershipStatus.Invited && !legacyPending) return; // accepted or disabled: nothing to resend
        // Seats invited before T-010 were created active; their new link must go through the pending state.
        if (legacyPending) m.Status = MembershipStatus.Invited;

        await RevokeOldInvitationLinksAsync(m, ct);
        var token = IssueInvitation(m.User!, m.Id);
        await db.SaveChangesAsync(ct);
        await SendInvitationEmailAsync(m.User!, await clinic.GetTenantAsync(ct), token, !string.IsNullOrEmpty(m.User!.PasswordHash), ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Invalidates this person's unused invitation links — unless another clinic's invitation to them is also pending:
    /// codes don't record their seat (only the link does), so revoking them all would cancel the other clinic's link.
    /// A stale link of THIS seat is harmless anyway once the seat is accepted or disabled (accept requires "Invited").
    /// </summary>
    private async Task RevokeOldInvitationLinksAsync(TenantMembership m, CancellationToken ct)
    {
        var userId = m.UserId;
        var seatId = m.Id;
        var otherPending = await db.TenantMemberships.IgnoreQueryFilters(QueryFilters.TenantOnly)
            .AnyAsync(x => x.UserId == userId && x.Id != seatId && x.Status == MembershipStatus.Invited, ct);
        if (otherPending) return;
        var now = clock.UtcNow;
        await db.VerificationCodes.Where(c => c.UserId == userId && c.Purpose == VerificationPurpose.Invitation && c.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAtUtc, now), ct);
    }

    private async Task<TenantMembership> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var q = db.TenantMemberships.Include(m => m.User).Include(m => m.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .Include(m => m.Branches).AsQueryable();
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.StaffNotFound);
    }

    private void EnsureEditable(TenantMembership m)
    {
        if (m.IsOwner) throw new ConflictException(ErrorCodes.OwnerProtected);
        if (m.UserId == actor.UserId) throw new ConflictException(ErrorCodes.CannotChangeSelf);
    }

    private async Task<List<Role>> LoadAssignableRolesAsync(IReadOnlyList<Guid> roleIds, CancellationToken ct, bool allowEmpty = false)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0 && allowEmpty) return [];
        var roles = await db.Roles.Include(r => r.Permissions).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        // A foreign role id is filtered out by the tenant filter and shows up here as "not found".
        if (roles.Count != ids.Count) throw new RequestValidationException(new Dictionary<string, string[]> { ["roleIds"] = [ValidationCodes.InvalidValue] });
        // Ownership is transferred explicitly, never handed out through staff management.
        if (roles.Any(r => r.SystemKey == SystemRoles.TenantOwner)) throw new ForbiddenException(ErrorCodes.PermissionEscalation);
        EnsureWithinActorPermissions(roles);
        return roles;
    }

    private void EnsureWithinActorPermissions(IEnumerable<Role> roles)
    {
        if (roles.SelectMany(r => r.Permissions).Any(p => !actor.HasPermission(p.Permission)))
            throw new ForbiddenException(ErrorCodes.PermissionEscalation);
    }

    /// <summary>
    /// QA-01: a branch-restricted actor hands out access only inside their own branches — never "every branch"
    /// (an empty restriction). Unrestricted actors may assign any branch of the clinic, or none.
    /// </summary>
    private void EnsureAssignableBranches(IReadOnlyCollection<Guid> branchIds)
    {
        var own = actor.BranchIds;
        if (own.Count == 0) return;
        if (branchIds.Count == 0 || branchIds.Any(b => !own.Contains(b)))
            throw new ForbiddenException(ErrorCodes.BranchAccessDenied);
    }

    /// <summary>A branch-restricted actor manages only members whose current access lies entirely inside their branches.</summary>
    private void EnsureMemberWithinActorBranches(TenantMembership m) =>
        EnsureAssignableBranches(m.Branches.Select(b => b.BranchId).ToList());

    private async Task<List<Guid>> ValidateBranchesAsync(IReadOnlyList<Guid>? branchIds, CancellationToken ct)
    {
        var ids = branchIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return ids;
        var found = await db.Branches.CountAsync(b => ids.Contains(b.Id), ct);
        if (found != ids.Count) throw new RequestValidationException(new Dictionary<string, string[]> { ["branchIds"] = [ValidationCodes.InvalidValue] });
        return ids;
    }

    /// <summary>
    /// Actions only the account owner can cause, and only while holding an active seat in this clinic (or, for
    /// InvitationAccepted, by accepting this clinic's invitation). Any of them in this clinic's audit log proves the person
    /// joined this clinic at some point.
    /// </summary>
    private static readonly string[] JoinedEvidence = [AuditActions.InvitationAccepted, AuditActions.LoginSucceeded, AuditActions.TenantSwitched];

    /// <summary>
    /// QA-12: users who have actually been members of THIS clinic. The schema has no "accepted at" marker on a seat
    /// (DB-REQ-1), so acceptance is read from the append-only audit log, scoped to this clinic's id: a sign-in or
    /// acceptance at another clinic never counts. Fails closed — a seat without evidence stays masked.
    /// </summary>
    private IQueryable<Guid> JoinedUserIds()
    {
        var tenantId = clinic.TenantId;
        return db.AuditLogs.Where(a => a.TenantId == tenantId && a.UserId != null && JoinedEvidence.Contains(a.Action))
            .Select(a => a.UserId!.Value);
    }

    /// <summary>Disabled seats among <paramref name="seats"/> whose account did join this clinic before (see <see cref="JoinedUserIds"/>).</summary>
    private async Task<HashSet<Guid>> FormerMembersAsync(IEnumerable<TenantMembership> seats, CancellationToken ct)
    {
        var userIds = seats.Where(m => m.Status == MembershipStatus.Disabled).Select(m => m.UserId).Distinct().ToList();
        if (userIds.Count == 0) return [];
        return (await JoinedUserIds().Where(u => userIds.Contains(u)).Distinct().ToListAsync(ct)).ToHashSet();
    }

    private Task<Dictionary<Guid, Guid>> DoctorIdsAsync(IEnumerable<Guid> membershipIds, CancellationToken ct)
    {
        var ids = membershipIds.ToList();
        return db.Doctors.Where(d => ids.Contains(d.MembershipId)).ToDictionaryAsync(d => d.MembershipId, d => d.Id, ct);
    }

    /// <summary>A one-time link bound to <paramref name="membershipId"/> (see <see cref="InvitationTokens"/>).</summary>
    private string IssueInvitation(User user, Guid membershipId)
    {
        var raw = InvitationTokens.Compose(membershipId, tokens.CreateOpaqueToken());
        var now = clock.UtcNow;
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id,
            Purpose = VerificationPurpose.Invitation,
            Target = user.Email,
            CodeHash = tokens.Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(InvitationLifetime),
            CreatedByIp = request.IpAddress,
        });
        return raw;
    }

    /// <summary>The email goes to the account owner only, so it may say whether they need a password or a sign-in.</summary>
    private Task SendInvitationEmailAsync(User user, Tenant tenant, string token, bool existingAccount, CancellationToken ct)
    {
        var link = OneTimeLinks.Build(authOptions.Value.AppBaseUrl, "accept-invitation", token);
        const string cta = "قبول الدعوة";
        var body = existingAccount
            ? $"تمت دعوتك للانضمام إلى فريق {tenant.Name}. سجّل الدخول بحسابك الحالي ثم اقبل الدعوة من الرابط. لن تُضاف إلى الفريق قبل قبولك. الرابط صالح لمدة 7 أيام."
            : $"تمت دعوتك للانضمام إلى فريق {tenant.Name}. اضغط لتعيين كلمة المرور. الرابط صالح لمدة 7 أيام.";
        var html = $"""
            <div dir="rtl" style="font-family:'IBM Plex Sans Arabic',Tahoma,sans-serif;max-width:520px;color:#0f172a">
              <p>مرحباً {WebUtility.HtmlEncode(user.FullName)}،</p>
              <p>{WebUtility.HtmlEncode(body)}</p>
              <p><a href="{WebUtility.HtmlEncode(link)}" style="background:#0d9488;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none">{cta}</a></p>
            </div>
            """;
        return email.TrySendAsync(logger, "staff-invitation", new EmailMessage(user.Email, $"دعوة للانضمام إلى {tenant.Name}", html), ct);
    }

    /// <summary>
    /// QA-02: until the account owner accepts, a pending seat shows nothing of the global account — not its name,
    /// phone, stored email casing, confirmation/password state, last login, or its (sequential, so datable) user id.
    /// The same masked view is returned whether the email was registered or not.
    /// QA-12: disabling a pending seat does not make the account this clinic's data — a disabled seat stays masked
    /// unless the person joined this clinic before (<paramref name="formerMembers"/>).
    /// </summary>
    private static StaffMemberDto ToDto(TenantMembership m, IReadOnlyDictionary<Guid, Guid> doctors, IReadOnlySet<Guid> formerMembers)
    {
        var roles = m.Roles.Select(r => new RoleRef(r.RoleId, r.Role?.Name ?? "")).ToList();
        var branches = m.Branches.Select(b => b.BranchId).ToList();
        Guid? doctorId = doctors.TryGetValue(m.Id, out var d) ? d : null;
        if (m.Status == MembershipStatus.Invited || (m.Status == MembershipStatus.Disabled && !formerMembers.Contains(m.UserId)))
        {
            var invitedEmail = m.User!.NormalizedEmail.ToLowerInvariant();
            return new StaffMemberDto(m.Id, Guid.Empty, invitedEmail, invitedEmail, null, m.Status, m.IsOwner, false, false,
                roles, branches, doctorId, null, RowVersions.Encode(m.RowVersion));
        }
        return new StaffMemberDto(m.Id, m.UserId, m.User!.FullName, m.User.Email, m.User.PhoneNumber, m.Status, m.IsOwner,
            m.User.EmailConfirmed, !string.IsNullOrEmpty(m.User.PasswordHash), roles, branches, doctorId,
            m.User.LastLoginAtUtc, RowVersions.Encode(m.RowVersion));
    }
}
