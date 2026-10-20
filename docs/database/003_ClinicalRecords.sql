BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    ALTER TABLE [Appointments] ADD CONSTRAINT [AK_Appointments_TenantId_Id] UNIQUE ([TenantId], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE TABLE [Visits] (
        [Id] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [DoctorId] uniqueidentifier NOT NULL,
        [AppointmentId] uniqueidentifier NULL,
        [VisitedAtUtc] datetime2(3) NOT NULL,
        [Symptoms] nvarchar(1000) NULL,
        [Diagnosis] nvarchar(1000) NULL,
        [DiagnosisCode] varchar(20) NULL,
        [ClinicalNotes] nvarchar(4000) NULL,
        [TreatmentPlan] nvarchar(2000) NULL,
        [SystolicBp] int NULL,
        [DiastolicBp] int NULL,
        [HeartRate] int NULL,
        [RespiratoryRate] int NULL,
        [TemperatureC] decimal(4,1) NULL,
        [OxygenSaturation] int NULL,
        [WeightKg] decimal(5,2) NULL,
        [HeightCm] decimal(5,1) NULL,
        [FollowUpDate] date NULL,
        [FollowUpNotes] nvarchar(500) NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAtUtc] datetime2(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [UpdatedAtUtc] datetime2(3) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [TenantId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Visits] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Visits_TenantId_Id] UNIQUE ([TenantId], [Id]),
        CONSTRAINT [CK_Visits_Bp] CHECK (([SystolicBp] IS NULL OR [SystolicBp] BETWEEN 40 AND 300) AND ([DiastolicBp] IS NULL OR [DiastolicBp] BETWEEN 20 AND 200)),
        CONSTRAINT [CK_Visits_Spo2] CHECK ([OxygenSaturation] IS NULL OR [OxygenSaturation] BETWEEN 40 AND 100),
        CONSTRAINT [FK_Visits_Appointments_TenantId_AppointmentId] FOREIGN KEY ([TenantId], [AppointmentId]) REFERENCES [Appointments] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Visits_Branches_TenantId_BranchId] FOREIGN KEY ([TenantId], [BranchId]) REFERENCES [Branches] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Visits_DoctorProfiles_TenantId_DoctorId] FOREIGN KEY ([TenantId], [DoctorId]) REFERENCES [DoctorProfiles] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Visits_Patients_TenantId_PatientId] FOREIGN KEY ([TenantId], [PatientId]) REFERENCES [Patients] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Visits_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE TABLE [FileAttachments] (
        [Id] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [VisitId] uniqueidentifier NULL,
        [FileName] nvarchar(255) NOT NULL,
        [ContentType] varchar(100) NOT NULL,
        [SizeBytes] bigint NOT NULL,
        [StorageKey] varchar(300) NOT NULL,
        [Sha256] char(64) NOT NULL,
        [Category] nvarchar(32) NOT NULL,
        [Description] nvarchar(500) NULL,
        [ScanStatus] nvarchar(32) NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAtUtc] datetime2(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [UpdatedAtUtc] datetime2(3) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [TenantId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_FileAttachments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_FileAttachments_ScanStatus] CHECK ([ScanStatus] IN ('Pending','Scanning','Clean','Rejected','Failed')),
        CONSTRAINT [CK_FileAttachments_Size] CHECK ([SizeBytes] > 0),
        CONSTRAINT [FK_FileAttachments_Patients_TenantId_PatientId] FOREIGN KEY ([TenantId], [PatientId]) REFERENCES [Patients] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FileAttachments_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FileAttachments_Visits_TenantId_VisitId] FOREIGN KEY ([TenantId], [VisitId]) REFERENCES [Visits] ([TenantId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE TABLE [Prescriptions] (
        [Id] uniqueidentifier NOT NULL,
        [Number] varchar(20) NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [DoctorId] uniqueidentifier NOT NULL,
        [VisitId] uniqueidentifier NULL,
        [IssuedAtUtc] datetime2(3) NOT NULL,
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
        CONSTRAINT [PK_Prescriptions] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Prescriptions_TenantId_Id] UNIQUE ([TenantId], [Id]),
        CONSTRAINT [FK_Prescriptions_DoctorProfiles_TenantId_DoctorId] FOREIGN KEY ([TenantId], [DoctorId]) REFERENCES [DoctorProfiles] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Prescriptions_Patients_TenantId_PatientId] FOREIGN KEY ([TenantId], [PatientId]) REFERENCES [Patients] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Prescriptions_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Prescriptions_Visits_TenantId_VisitId] FOREIGN KEY ([TenantId], [VisitId]) REFERENCES [Visits] ([TenantId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE TABLE [PrescriptionItems] (
        [Id] uniqueidentifier NOT NULL,
        [PrescriptionId] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [MedicationName] nvarchar(200) NOT NULL,
        [Strength] nvarchar(100) NULL,
        [Dosage] nvarchar(100) NULL,
        [Frequency] nvarchar(100) NULL,
        [Route] nvarchar(50) NULL,
        [DurationDays] int NULL,
        [Instructions] nvarchar(500) NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [UpdatedAtUtc] datetime2(3) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [TenantId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_PrescriptionItems] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PrescriptionItems_Duration] CHECK ([DurationDays] IS NULL OR [DurationDays] BETWEEN 1 AND 365),
        CONSTRAINT [FK_PrescriptionItems_Prescriptions_TenantId_PrescriptionId] FOREIGN KEY ([TenantId], [PrescriptionId]) REFERENCES [Prescriptions] ([TenantId], [Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FileAttachments_StorageKey] ON [FileAttachments] ([StorageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_FileAttachments_TenantId_PatientId_CreatedAtUtc] ON [FileAttachments] ([TenantId], [PatientId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_FileAttachments_TenantId_VisitId] ON [FileAttachments] ([TenantId], [VisitId]) WHERE [VisitId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PrescriptionItems_TenantId_PrescriptionId_LineNumber] ON [PrescriptionItems] ([TenantId], [PrescriptionId], [LineNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_Prescriptions_TenantId_DoctorId] ON [Prescriptions] ([TenantId], [DoctorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Prescriptions_TenantId_Number] ON [Prescriptions] ([TenantId], [Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_Prescriptions_TenantId_PatientId_IssuedAtUtc] ON [Prescriptions] ([TenantId], [PatientId], [IssuedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Prescriptions_TenantId_VisitId] ON [Prescriptions] ([TenantId], [VisitId]) WHERE [VisitId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_Visits_TenantId_BranchId_VisitedAtUtc] ON [Visits] ([TenantId], [BranchId], [VisitedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_Visits_TenantId_DoctorId_VisitedAtUtc] ON [Visits] ([TenantId], [DoctorId], [VisitedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    CREATE INDEX [IX_Visits_TenantId_PatientId_VisitedAtUtc] ON [Visits] ([TenantId], [PatientId], [VisitedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Visits_Appointment] ON [Visits] ([TenantId], [AppointmentId]) WHERE [AppointmentId] IS NOT NULL AND [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006151239_ClinicalRecords'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006151239_ClinicalRecords', N'10.0.12');
END;

COMMIT;
GO

