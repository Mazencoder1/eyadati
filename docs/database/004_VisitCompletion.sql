BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    ALTER TABLE [Visits] ADD [CompletedAtUtc] datetime2(3) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    ALTER TABLE [Visits] ADD [CompletedByUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    ALTER TABLE [Visits] ADD [Status] nvarchar(32) NOT NULL DEFAULT N'Open';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    EXEC(N'ALTER TABLE [Visits] ADD CONSTRAINT [CK_Visits_Completion] CHECK (([Status] = ''Completed'' AND [CompletedAtUtc] IS NOT NULL) OR ([Status] <> ''Completed'' AND [CompletedAtUtc] IS NULL AND [CompletedByUserId] IS NULL))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    EXEC(N'ALTER TABLE [Visits] ADD CONSTRAINT [CK_Visits_Status] CHECK ([Status] IN (''Open'',''Completed''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006181912_VisitCompletion'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006181912_VisitCompletion', N'10.0.12');
END;

COMMIT;
GO

