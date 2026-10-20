using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ClinicSaaS.Application.Tenancy;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Api.Controllers;

/// <summary>Public plan catalog for the pricing page. Non-sensitive and cacheable.</summary>
[ApiController]
[Route("api/v1/plans")]
/// Only what the product really has is advertised: features in FeatureKeys.Offered and limits in LimitKeys.Enforced
/// (other rows may exist in plan data for planned capabilities); plans in Saas:CatalogHiddenPlanCodes are left out.
public sealed class PlansController(IAppDbContext db, IOptions<SaasOptions> saas) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [OutputCache(Duration = 300)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PublicPlan>>>> List(CancellationToken ct)
    {
        var hidden = saas.Value.CatalogHiddenPlanCodes;
        var rows = await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && p.IsPublic && !hidden.Contains(p.Code))
            .OrderBy(p => p.SortOrder)
            .Include(p => p.Features)
            .Include(p => p.Limits)
            .ToListAsync(ct);
        var plans = rows.Select(p => new PublicPlan(p.Code, p.NameAr, p.NameEn, p.DescriptionAr, p.DescriptionEn, p.MonthlyPrice,
                p.YearlyPrice, p.Currency, p.TrialDays,
                p.Features.Select(f => f.FeatureKey).Where(FeatureKeys.Offered.Contains).Order().ToList(),
                p.Limits.Where(l => LimitKeys.Enforced.Contains(l.LimitKey)).ToDictionary(l => l.LimitKey, l => l.MaxValue)))
            .ToList();
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new ApiResponse<IReadOnlyList<PublicPlan>>(plans));
    }
}

public sealed record PublicPlan(
    string Code, string NameAr, string NameEn, string? DescriptionAr, string? DescriptionEn,
    decimal MonthlyPrice, decimal YearlyPrice, string Currency, int TrialDays,
    IReadOnlyList<string> Features, IReadOnlyDictionary<string, int?> Limits);
