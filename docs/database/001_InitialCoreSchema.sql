IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [AuditLogs] (
    [Id] bigint NOT NULL IDENTITY,
    [TenantId] uniqueidentifier NULL,
    [UserId] uniqueidentifier NULL,
    [OccurredAtUtc] datetime2(3) NOT NULL,
    [Action] varchar(100) NOT NULL,
    [EntityType] varchar(100) NULL,
    [EntityId] varchar(64) NULL,
    [Result] nvarchar(32) NOT NULL,
    [IpAddress] varchar(45) NULL,
    [UserAgent] nvarchar(256) NULL,
    [CorrelationId] varchar(64) NULL,
    [Details] nvarchar(2000) NULL,
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
);

CREATE TABLE [Plans] (
    [Id] uniqueidentifier NOT NULL,
    [Code] varchar(50) NOT NULL,
    [NameAr] nvarchar(100) NOT NULL,
    [NameEn] nvarchar(100) NOT NULL,
    [DescriptionAr] nvarchar(500) NULL,
    [DescriptionEn] nvarchar(500) NULL,
    [MonthlyPrice] decimal(18,2) NOT NULL,
    [YearlyPrice] decimal(18,2) NOT NULL,
    [Currency] char(3) NOT NULL,
    [TrialDays] int NOT NULL,
    [IsPublic] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [SortOrder] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    CONSTRAINT [PK_Plans] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Plans_Prices] CHECK ([MonthlyPrice] >= 0 AND [YearlyPrice] >= 0),
    CONSTRAINT [CK_Plans_TrialDays] CHECK ([TrialDays] BETWEEN 0 AND 90)
);

CREATE TABLE [Tenants] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Slug] varchar(63) NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [LegalName] nvarchar(200) NULL,
    [VatNumber] varchar(15) NULL,
    [CommercialRegistration] varchar(20) NULL,
    [Email] nvarchar(256) NULL,
    [Phone] varchar(20) NULL,
    [City] nvarchar(100) NULL,
    [TimeZone] varchar(64) NOT NULL,
    [Currency] char(3) NOT NULL,
    [Locale] varchar(10) NOT NULL,
    [DefaultAppointmentMinutes] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Tenants_DefaultAppointmentMinutes] CHECK ([DefaultAppointmentMinutes] BETWEEN 5 AND 240)
);

CREATE TABLE [Users] (
    [Id] uniqueidentifier NOT NULL,
    [Email] nvarchar(256) NOT NULL,
    [NormalizedEmail] nvarchar(256) NOT NULL,
    [PasswordHash] varchar(512) NOT NULL,
    [FullName] nvarchar(200) NOT NULL,
    [PhoneNumber] varchar(20) NULL,
    [EmailConfirmed] bit NOT NULL,
    [PhoneNumberConfirmed] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [PlatformRole] nvarchar(32) NOT NULL,
    [PreferredLocale] varchar(10) NOT NULL,
    [AccessFailedCount] int NOT NULL,
    [LockoutEndUtc] datetime2(3) NULL,
    [SecurityStamp] varchar(64) NOT NULL,
    [LastLoginAtUtc] datetime2(3) NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);

CREATE TABLE [PlanFeatures] (
    [PlanId] uniqueidentifier NOT NULL,
    [FeatureKey] varchar(64) NOT NULL,
    CONSTRAINT [PK_PlanFeatures] PRIMARY KEY ([PlanId], [FeatureKey]),
    CONSTRAINT [FK_PlanFeatures_Plans_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Plans] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [PlanLimits] (
    [PlanId] uniqueidentifier NOT NULL,
    [LimitKey] varchar(64) NOT NULL,
    [MaxValue] int NULL,
    CONSTRAINT [PK_PlanLimits] PRIMARY KEY ([PlanId], [LimitKey]),
    CONSTRAINT [CK_PlanLimits_MaxValue] CHECK ([MaxValue] IS NULL OR [MaxValue] >= 0),
    CONSTRAINT [FK_PlanLimits_Plans_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Plans] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Branches] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [City] nvarchar(100) NULL,
    [Address] nvarchar(500) NULL,
    [Phone] varchar(20) NULL,
    [Latitude] decimal(9,6) NULL,
    [Longitude] decimal(9,6) NULL,
    [IsMain] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [DeletedAtUtc] datetime2(3) NULL,
    [DeletedByUserId] uniqueidentifier NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Branches] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_Branches_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [FK_Branches_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [ClinicServices] (
    [Id] uniqueidentifier NOT NULL,
    [NameAr] nvarchar(200) NOT NULL,
    [NameEn] nvarchar(200) NULL,
    [DurationMinutes] int NOT NULL,
    [Price] decimal(18,2) NOT NULL,
    [IsActive] bit NOT NULL,
    [IsBookableOnline] bit NOT NULL,
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [DeletedAtUtc] datetime2(3) NULL,
    [DeletedByUserId] uniqueidentifier NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_ClinicServices] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_ClinicServices_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [CK_ClinicServices_Duration] CHECK ([DurationMinutes] BETWEEN 5 AND 480),
    CONSTRAINT [CK_ClinicServices_Price] CHECK ([Price] >= 0),
    CONSTRAINT [FK_ClinicServices_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [NumberSequences] (
    [TenantId] uniqueidentifier NOT NULL,
    [Name] varchar(50) NOT NULL,
    [NextValue] bigint NOT NULL,
    CONSTRAINT [PK_NumberSequences] PRIMARY KEY ([TenantId], [Name]),
    CONSTRAINT [CK_NumberSequences_NextValue] CHECK ([NextValue] >= 1),
    CONSTRAINT [FK_NumberSequences_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [Roles] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [NormalizedName] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [SystemKey] varchar(50) NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_Roles_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [FK_Roles_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [Subscriptions] (
    [Id] uniqueidentifier NOT NULL,
    [PlanId] uniqueidentifier NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [BillingCycle] nvarchar(32) NOT NULL,
    [StartedAtUtc] datetime2(3) NOT NULL,
    [CurrentPeriodStartUtc] datetime2(3) NOT NULL,
    [CurrentPeriodEndUtc] datetime2(3) NOT NULL,
    [TrialEndsAtUtc] datetime2(3) NULL,
    [CancelAtPeriodEnd] bit NOT NULL,
    [CanceledAtUtc] datetime2(3) NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Subscriptions] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Subscriptions_Period] CHECK ([CurrentPeriodEndUtc] > [CurrentPeriodStartUtc]),
    CONSTRAINT [FK_Subscriptions_Plans_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Plans] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Subscriptions_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [TenantFeatureOverrides] (
    [TenantId] uniqueidentifier NOT NULL,
    [FeatureKey] varchar(64) NOT NULL,
    [IsEnabled] bit NOT NULL,
    CONSTRAINT [PK_TenantFeatureOverrides] PRIMARY KEY ([TenantId], [FeatureKey]),
    CONSTRAINT [FK_TenantFeatureOverrides_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [RefreshTokens] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NULL,
    [TokenHash] char(64) NOT NULL,
    [FamilyId] uniqueidentifier NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [ExpiresAtUtc] datetime2(3) NOT NULL,
    [CreatedByIp] varchar(45) NULL,
    [UserAgent] nvarchar(256) NULL,
    [RevokedAtUtc] datetime2(3) NULL,
    [RevokedReason] varchar(50) NULL,
    [ReplacedByTokenId] uniqueidentifier NULL,
    CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RefreshTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [TenantMemberships] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [IsOwner] bit NOT NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_TenantMemberships] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_TenantMemberships_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [FK_TenantMemberships_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_TenantMemberships_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [VerificationCodes] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Purpose] nvarchar(32) NOT NULL,
    [Target] nvarchar(256) NOT NULL,
    [CodeHash] char(64) NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [ExpiresAtUtc] datetime2(3) NOT NULL,
    [ConsumedAtUtc] datetime2(3) NULL,
    [Attempts] int NOT NULL,
    [CreatedByIp] varchar(45) NULL,
    CONSTRAINT [PK_VerificationCodes] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_VerificationCodes_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Patients] (
    [Id] uniqueidentifier NOT NULL,
    [MedicalRecordNumber] varchar(20) NOT NULL,
    [FullName] nvarchar(200) NOT NULL,
    [FullNameEn] nvarchar(200) NULL,
    [MobileNumber] varchar(20) NOT NULL,
    [Email] nvarchar(256) NULL,
    [IdentityType] nvarchar(32) NOT NULL,
    [IdentityNumber] varchar(20) NULL,
    [DateOfBirth] date NULL,
    [Gender] nvarchar(32) NOT NULL,
    [Nationality] char(2) NULL,
    [RegisteredBranchId] uniqueidentifier NULL,
    [BloodGroup] nvarchar(32) NULL,
    [Allergies] nvarchar(1000) NULL,
    [ChronicConditions] nvarchar(1000) NULL,
    [MedicalNotes] nvarchar(2000) NULL,
    [Notes] nvarchar(1000) NULL,
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [DeletedAtUtc] datetime2(3) NULL,
    [DeletedByUserId] uniqueidentifier NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Patients] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_Patients_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [FK_Patients_Branches_TenantId_RegisteredBranchId] FOREIGN KEY ([TenantId], [RegisteredBranchId]) REFERENCES [Branches] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Patients_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [RolePermissions] (
    [RoleId] uniqueidentifier NOT NULL,
    [Permission] varchar(100) NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleId], [Permission]),
    CONSTRAINT [FK_RolePermissions_Roles_TenantId_RoleId] FOREIGN KEY ([TenantId], [RoleId]) REFERENCES [Roles] ([TenantId], [Id]) ON DELETE CASCADE
);

CREATE TABLE [DoctorProfiles] (
    [Id] uniqueidentifier NOT NULL,
    [MembershipId] uniqueidentifier NOT NULL,
    [DisplayNameAr] nvarchar(200) NOT NULL,
    [DisplayNameEn] nvarchar(200) NULL,
    [Specialty] nvarchar(100) NOT NULL,
    [LicenseNumber] varchar(50) NULL,
    [Bio] nvarchar(1000) NULL,
    [ConsultationFee] decimal(18,2) NOT NULL,
    [DefaultSlotMinutes] int NOT NULL,
    [AcceptsOnlineBooking] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [DeletedAtUtc] datetime2(3) NULL,
    [DeletedByUserId] uniqueidentifier NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_DoctorProfiles] PRIMARY KEY ([Id]),
    CONSTRAINT [AK_DoctorProfiles_TenantId_Id] UNIQUE ([TenantId], [Id]),
    CONSTRAINT [CK_DoctorProfiles_Fee] CHECK ([ConsultationFee] >= 0),
    CONSTRAINT [CK_DoctorProfiles_Slot] CHECK ([DefaultSlotMinutes] BETWEEN 5 AND 240),
    CONSTRAINT [FK_DoctorProfiles_TenantMemberships_TenantId_MembershipId] FOREIGN KEY ([TenantId], [MembershipId]) REFERENCES [TenantMemberships] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_DoctorProfiles_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [MembershipBranches] (
    [MembershipId] uniqueidentifier NOT NULL,
    [BranchId] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_MembershipBranches] PRIMARY KEY ([MembershipId], [BranchId]),
    CONSTRAINT [FK_MembershipBranches_Branches_TenantId_BranchId] FOREIGN KEY ([TenantId], [BranchId]) REFERENCES [Branches] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_MembershipBranches_TenantMemberships_TenantId_MembershipId] FOREIGN KEY ([TenantId], [MembershipId]) REFERENCES [TenantMemberships] ([TenantId], [Id]) ON DELETE CASCADE
);

CREATE TABLE [MembershipRoles] (
    [MembershipId] uniqueidentifier NOT NULL,
    [RoleId] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_MembershipRoles] PRIMARY KEY ([MembershipId], [RoleId]),
    CONSTRAINT [FK_MembershipRoles_Roles_TenantId_RoleId] FOREIGN KEY ([TenantId], [RoleId]) REFERENCES [Roles] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_MembershipRoles_TenantMemberships_TenantId_MembershipId] FOREIGN KEY ([TenantId], [MembershipId]) REFERENCES [TenantMemberships] ([TenantId], [Id]) ON DELETE CASCADE
);

CREATE TABLE [Appointments] (
    [Id] uniqueidentifier NOT NULL,
    [BranchId] uniqueidentifier NOT NULL,
    [PatientId] uniqueidentifier NOT NULL,
    [DoctorId] uniqueidentifier NOT NULL,
    [ServiceId] uniqueidentifier NULL,
    [StartUtc] datetime2(3) NOT NULL,
    [EndUtc] datetime2(3) NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [Source] nvarchar(32) NOT NULL,
    [TokenNumber] varchar(10) NULL,
    [Reason] nvarchar(500) NULL,
    [CancellationReason] nvarchar(500) NULL,
    [ConfirmedAtUtc] datetime2(3) NULL,
    [CheckedInAtUtc] datetime2(3) NULL,
    [CompletedAtUtc] datetime2(3) NULL,
    [CancelledAtUtc] datetime2(3) NULL,
    [RowVersion] rowversion NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Appointments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Appointments_Range] CHECK ([EndUtc] > [StartUtc]),
    CONSTRAINT [FK_Appointments_Branches_TenantId_BranchId] FOREIGN KEY ([TenantId], [BranchId]) REFERENCES [Branches] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Appointments_ClinicServices_TenantId_ServiceId] FOREIGN KEY ([TenantId], [ServiceId]) REFERENCES [ClinicServices] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Appointments_DoctorProfiles_TenantId_DoctorId] FOREIGN KEY ([TenantId], [DoctorId]) REFERENCES [DoctorProfiles] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Appointments_Patients_TenantId_PatientId] FOREIGN KEY ([TenantId], [PatientId]) REFERENCES [Patients] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Appointments_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [DoctorSchedules] (
    [Id] uniqueidentifier NOT NULL,
    [DoctorId] uniqueidentifier NOT NULL,
    [BranchId] uniqueidentifier NOT NULL,
    [DayOfWeek] tinyint NOT NULL,
    [StartTime] time(0) NOT NULL,
    [EndTime] time(0) NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    [CreatedByUserId] uniqueidentifier NULL,
    [UpdatedAtUtc] datetime2(3) NULL,
    [UpdatedByUserId] uniqueidentifier NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_DoctorSchedules] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_DoctorSchedules_Day] CHECK ([DayOfWeek] BETWEEN 0 AND 6),
    CONSTRAINT [CK_DoctorSchedules_Range] CHECK ([EndTime] > [StartTime]),
    CONSTRAINT [FK_DoctorSchedules_Branches_TenantId_BranchId] FOREIGN KEY ([TenantId], [BranchId]) REFERENCES [Branches] ([TenantId], [Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_DoctorSchedules_DoctorProfiles_TenantId_DoctorId] FOREIGN KEY ([TenantId], [DoctorId]) REFERENCES [DoctorProfiles] ([TenantId], [Id]) ON DELETE CASCADE
);

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'CreatedAtUtc', N'CreatedByUserId', N'Currency', N'DescriptionAr', N'DescriptionEn', N'IsActive', N'IsPublic', N'MonthlyPrice', N'NameAr', N'NameEn', N'SortOrder', N'TrialDays', N'UpdatedAtUtc', N'UpdatedByUserId', N'YearlyPrice') AND [object_id] = OBJECT_ID(N'[Plans]'))
    SET IDENTITY_INSERT [Plans] ON;
INSERT INTO [Plans] ([Id], [Code], [CreatedAtUtc], [CreatedByUserId], [Currency], [DescriptionAr], [DescriptionEn], [IsActive], [IsPublic], [MonthlyPrice], [NameAr], [NameEn], [SortOrder], [TrialDays], [UpdatedAtUtc], [UpdatedByUserId], [YearlyPrice])
VALUES ('0b6a4a3e-0000-4000-8000-000000000001', 'free', '2026-10-06T00:00:00.000Z', NULL, 'SAR', NULL, NULL, CAST(1 AS bit), CAST(1 AS bit), 0.0, N'المجانية', N'Free', 1, 0, NULL, NULL, 0.0),
('0b6a4a3e-0000-4000-8000-000000000002', 'basic', '2026-10-06T00:00:00.000Z', NULL, 'SAR', NULL, NULL, CAST(1 AS bit), CAST(1 AS bit), 199.0, N'الأساسية', N'Basic', 2, 14, NULL, NULL, 1990.0),
('0b6a4a3e-0000-4000-8000-000000000003', 'professional', '2026-10-06T00:00:00.000Z', NULL, 'SAR', NULL, NULL, CAST(1 AS bit), CAST(1 AS bit), 449.0, N'الاحترافية', N'Professional', 3, 14, NULL, NULL, 4490.0),
('0b6a4a3e-0000-4000-8000-000000000004', 'enterprise', '2026-10-06T00:00:00.000Z', NULL, 'SAR', NULL, NULL, CAST(1 AS bit), CAST(1 AS bit), 1499.0, N'المؤسسات', N'Enterprise', 4, 14, NULL, NULL, 14990.0);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'CreatedAtUtc', N'CreatedByUserId', N'Currency', N'DescriptionAr', N'DescriptionEn', N'IsActive', N'IsPublic', N'MonthlyPrice', N'NameAr', N'NameEn', N'SortOrder', N'TrialDays', N'UpdatedAtUtc', N'UpdatedByUserId', N'YearlyPrice') AND [object_id] = OBJECT_ID(N'[Plans]'))
    SET IDENTITY_INSERT [Plans] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'FeatureKey', N'PlanId') AND [object_id] = OBJECT_ID(N'[PlanFeatures]'))
    SET IDENTITY_INSERT [PlanFeatures] ON;
INSERT INTO [PlanFeatures] ([FeatureKey], [PlanId])
VALUES ('invoicing', '0b6a4a3e-0000-4000-8000-000000000001'),
('export', '0b6a4a3e-0000-4000-8000-000000000002'),
('invoicing', '0b6a4a3e-0000-4000-8000-000000000002'),
('online_booking', '0b6a4a3e-0000-4000-8000-000000000002'),
('audit_logs', '0b6a4a3e-0000-4000-8000-000000000003'),
('export', '0b6a4a3e-0000-4000-8000-000000000003'),
('invoicing', '0b6a4a3e-0000-4000-8000-000000000003'),
('multi_branch', '0b6a4a3e-0000-4000-8000-000000000003'),
('online_booking', '0b6a4a3e-0000-4000-8000-000000000003'),
('public_page_custom', '0b6a4a3e-0000-4000-8000-000000000003'),
('reports', '0b6a4a3e-0000-4000-8000-000000000003'),
('sms', '0b6a4a3e-0000-4000-8000-000000000003'),
('whatsapp', '0b6a4a3e-0000-4000-8000-000000000003'),
('api_access', '0b6a4a3e-0000-4000-8000-000000000004'),
('audit_logs', '0b6a4a3e-0000-4000-8000-000000000004'),
('export', '0b6a4a3e-0000-4000-8000-000000000004'),
('invoicing', '0b6a4a3e-0000-4000-8000-000000000004'),
('multi_branch', '0b6a4a3e-0000-4000-8000-000000000004'),
('online_booking', '0b6a4a3e-0000-4000-8000-000000000004'),
('public_page_custom', '0b6a4a3e-0000-4000-8000-000000000004'),
('reports', '0b6a4a3e-0000-4000-8000-000000000004'),
('sms', '0b6a4a3e-0000-4000-8000-000000000004'),
('whatsapp', '0b6a4a3e-0000-4000-8000-000000000004');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'FeatureKey', N'PlanId') AND [object_id] = OBJECT_ID(N'[PlanFeatures]'))
    SET IDENTITY_INSERT [PlanFeatures] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'LimitKey', N'PlanId', N'MaxValue') AND [object_id] = OBJECT_ID(N'[PlanLimits]'))
    SET IDENTITY_INSERT [PlanLimits] ON;
INSERT INTO [PlanLimits] ([LimitKey], [PlanId], [MaxValue])
VALUES ('max_appointments_month', '0b6a4a3e-0000-4000-8000-000000000001', 150),
('max_branches', '0b6a4a3e-0000-4000-8000-000000000001', 1),
('max_doctors', '0b6a4a3e-0000-4000-8000-000000000001', 1),
('max_patients', '0b6a4a3e-0000-4000-8000-000000000001', 100),
('max_sms_month', '0b6a4a3e-0000-4000-8000-000000000001', 0),
('max_storage_mb', '0b6a4a3e-0000-4000-8000-000000000001', 200),
('max_users', '0b6a4a3e-0000-4000-8000-000000000001', 1),
('max_whatsapp_month', '0b6a4a3e-0000-4000-8000-000000000001', 0),
('max_appointments_month', '0b6a4a3e-0000-4000-8000-000000000002', 1500),
('max_branches', '0b6a4a3e-0000-4000-8000-000000000002', 1),
('max_doctors', '0b6a4a3e-0000-4000-8000-000000000002', 2),
('max_patients', '0b6a4a3e-0000-4000-8000-000000000002', 3000),
('max_sms_month', '0b6a4a3e-0000-4000-8000-000000000002', 100),
('max_storage_mb', '0b6a4a3e-0000-4000-8000-000000000002', 2048),
('max_users', '0b6a4a3e-0000-4000-8000-000000000002', 3),
('max_whatsapp_month', '0b6a4a3e-0000-4000-8000-000000000002', 0),
('max_appointments_month', '0b6a4a3e-0000-4000-8000-000000000003', NULL),
('max_branches', '0b6a4a3e-0000-4000-8000-000000000003', 3),
('max_doctors', '0b6a4a3e-0000-4000-8000-000000000003', 10),
('max_patients', '0b6a4a3e-0000-4000-8000-000000000003', NULL),
('max_sms_month', '0b6a4a3e-0000-4000-8000-000000000003', 1000),
('max_storage_mb', '0b6a4a3e-0000-4000-8000-000000000003', 20480),
('max_users', '0b6a4a3e-0000-4000-8000-000000000003', 15),
('max_whatsapp_month', '0b6a4a3e-0000-4000-8000-000000000003', 1000),
('max_appointments_month', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_branches', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_doctors', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_patients', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_sms_month', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_storage_mb', '0b6a4a3e-0000-4000-8000-000000000004', 102400),
('max_users', '0b6a4a3e-0000-4000-8000-000000000004', NULL),
('max_whatsapp_month', '0b6a4a3e-0000-4000-8000-000000000004', NULL);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'LimitKey', N'PlanId', N'MaxValue') AND [object_id] = OBJECT_ID(N'[PlanLimits]'))
    SET IDENTITY_INSERT [PlanLimits] OFF;

CREATE INDEX [IX_Appointments_Doctor_Start] ON [Appointments] ([TenantId], [DoctorId], [StartUtc]) INCLUDE ([EndUtc], [Status], [Source]);

CREATE INDEX [IX_Appointments_TenantId_BranchId_StartUtc] ON [Appointments] ([TenantId], [BranchId], [StartUtc]);

CREATE INDEX [IX_Appointments_TenantId_PatientId_StartUtc] ON [Appointments] ([TenantId], [PatientId], [StartUtc]);

CREATE INDEX [IX_Appointments_TenantId_ServiceId] ON [Appointments] ([TenantId], [ServiceId]);

CREATE UNIQUE INDEX [UX_Appointments_ActiveSlot] ON [Appointments] ([TenantId], [DoctorId], [StartUtc]) WHERE [Status] IN ('Scheduled','Confirmed','CheckedIn') AND [Source] <> 'WalkIn';

CREATE INDEX [IX_AuditLogs_TenantId_EntityType_EntityId] ON [AuditLogs] ([TenantId], [EntityType], [EntityId]);

CREATE INDEX [IX_AuditLogs_TenantId_OccurredAtUtc] ON [AuditLogs] ([TenantId], [OccurredAtUtc]);

CREATE INDEX [IX_AuditLogs_UserId_OccurredAtUtc] ON [AuditLogs] ([UserId], [OccurredAtUtc]);

CREATE INDEX [IX_ClinicServices_TenantId_NameAr] ON [ClinicServices] ([TenantId], [NameAr]);

CREATE UNIQUE INDEX [IX_DoctorProfiles_TenantId_MembershipId] ON [DoctorProfiles] ([TenantId], [MembershipId]) WHERE [IsDeleted] = 0;

CREATE INDEX [IX_DoctorSchedules_TenantId_BranchId] ON [DoctorSchedules] ([TenantId], [BranchId]);

CREATE INDEX [IX_DoctorSchedules_TenantId_DoctorId_DayOfWeek] ON [DoctorSchedules] ([TenantId], [DoctorId], [DayOfWeek]);

CREATE INDEX [IX_MembershipBranches_TenantId_BranchId] ON [MembershipBranches] ([TenantId], [BranchId]);

CREATE INDEX [IX_MembershipBranches_TenantId_MembershipId] ON [MembershipBranches] ([TenantId], [MembershipId]);

CREATE INDEX [IX_MembershipRoles_TenantId_MembershipId] ON [MembershipRoles] ([TenantId], [MembershipId]);

CREATE INDEX [IX_MembershipRoles_TenantId_RoleId] ON [MembershipRoles] ([TenantId], [RoleId]);

CREATE INDEX [IX_Patients_TenantId_CreatedAtUtc] ON [Patients] ([TenantId], [CreatedAtUtc]);

CREATE INDEX [IX_Patients_TenantId_FullName] ON [Patients] ([TenantId], [FullName]);

CREATE INDEX [IX_Patients_TenantId_IdentityNumber] ON [Patients] ([TenantId], [IdentityNumber]) WHERE [IdentityNumber] IS NOT NULL;

CREATE UNIQUE INDEX [IX_Patients_TenantId_MedicalRecordNumber] ON [Patients] ([TenantId], [MedicalRecordNumber]);

CREATE INDEX [IX_Patients_TenantId_MobileNumber] ON [Patients] ([TenantId], [MobileNumber]);

CREATE INDEX [IX_Patients_TenantId_RegisteredBranchId] ON [Patients] ([TenantId], [RegisteredBranchId]);

CREATE UNIQUE INDEX [UX_Patients_Tenant_Identity] ON [Patients] ([TenantId], [IdentityType], [IdentityNumber]) WHERE [IdentityNumber] IS NOT NULL AND [IsDeleted] = 0;

CREATE UNIQUE INDEX [IX_Plans_Code] ON [Plans] ([Code]);

CREATE INDEX [IX_RefreshTokens_ExpiresAtUtc] ON [RefreshTokens] ([ExpiresAtUtc]);

CREATE INDEX [IX_RefreshTokens_FamilyId] ON [RefreshTokens] ([FamilyId]);

CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash] ON [RefreshTokens] ([TokenHash]);

CREATE INDEX [IX_RefreshTokens_UserId_RevokedAtUtc] ON [RefreshTokens] ([UserId], [RevokedAtUtc]);

CREATE INDEX [IX_RolePermissions_TenantId_RoleId] ON [RolePermissions] ([TenantId], [RoleId]);

CREATE UNIQUE INDEX [IX_Roles_TenantId_NormalizedName] ON [Roles] ([TenantId], [NormalizedName]);

CREATE INDEX [IX_Subscriptions_PlanId] ON [Subscriptions] ([PlanId]);

CREATE INDEX [IX_Subscriptions_Status_CurrentPeriodEndUtc] ON [Subscriptions] ([Status], [CurrentPeriodEndUtc]);

CREATE INDEX [IX_Subscriptions_TenantId_Status] ON [Subscriptions] ([TenantId], [Status]);

CREATE UNIQUE INDEX [UX_Subscriptions_Tenant_Current] ON [Subscriptions] ([TenantId]) WHERE [Status] IN ('Trialing','Active','PastDue');

CREATE UNIQUE INDEX [IX_TenantMemberships_TenantId_UserId] ON [TenantMemberships] ([TenantId], [UserId]);

CREATE INDEX [IX_TenantMemberships_UserId] ON [TenantMemberships] ([UserId]);

CREATE UNIQUE INDEX [IX_Tenants_Slug] ON [Tenants] ([Slug]);

CREATE INDEX [IX_Tenants_Status] ON [Tenants] ([Status]);

CREATE UNIQUE INDEX [IX_Users_NormalizedEmail] ON [Users] ([NormalizedEmail]);

CREATE INDEX [IX_Users_PlatformRole] ON [Users] ([PlatformRole]) WHERE [PlatformRole] <> 'None';

CREATE UNIQUE INDEX [IX_VerificationCodes_CodeHash] ON [VerificationCodes] ([CodeHash]);

CREATE INDEX [IX_VerificationCodes_UserId_Purpose] ON [VerificationCodes] ([UserId], [Purpose]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261006050208_InitialCoreSchema', N'10.0.12');

COMMIT;
GO

