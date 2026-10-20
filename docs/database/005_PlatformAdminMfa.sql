BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008035917_PlatformAdminMfa'
)
BEGIN
    CREATE TABLE [UserRecoveryCodes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CodeHash] char(64) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL,
        [ConsumedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_UserRecoveryCodes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserRecoveryCodes_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008035917_PlatformAdminMfa'
)
BEGIN
    CREATE TABLE [UserTotpFactors] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [SecretProtected] varbinary(256) NOT NULL,
        [LastUsedTimeStep] bigint NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL,
        CONSTRAINT [PK_UserTotpFactors] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserTotpFactors_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008035917_PlatformAdminMfa'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserRecoveryCodes_UserId_CodeHash] ON [UserRecoveryCodes] ([UserId], [CodeHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008035917_PlatformAdminMfa'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserTotpFactors_UserId] ON [UserTotpFactors] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008035917_PlatformAdminMfa'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008035917_PlatformAdminMfa', N'10.0.12');
END;

COMMIT;
GO

