BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006095223_AppointmentBoardIndexAndDurationGuard'
)
BEGIN
    CREATE INDEX [IX_Appointments_Tenant_Start] ON [Appointments] ([TenantId], [StartUtc]) INCLUDE ([Status], [Source]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006095223_AppointmentBoardIndexAndDurationGuard'
)
BEGIN
    EXEC(N'ALTER TABLE [Appointments] ADD CONSTRAINT [CK_Appointments_MaxDuration] CHECK (DATEDIFF(minute, [StartUtc], [EndUtc]) <= 480)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006095223_AppointmentBoardIndexAndDurationGuard'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006095223_AppointmentBoardIndexAndDurationGuard', N'10.0.12');
END;

COMMIT;
GO

