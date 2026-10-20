/*
  Least-privilege database principals (run once per environment by a DBA).
  - clinic_app:        the API at runtime. Data read/write only — cannot alter schema, cannot read other databases.
  - clinic_migrator:   applies reviewed migration scripts during deployment. Not used by the running API.
  Passwords are supplied by the secret store at execution time (sqlcmd variables), never committed.

  sqlcmd -S <server> -d ClinicSaaS -v AppPassword="..." MigratorPassword="..." -i least-privilege.sql
*/

CREATE LOGIN [clinic_app] WITH PASSWORD = '$(AppPassword)', CHECK_POLICY = ON;
CREATE USER [clinic_app] FOR LOGIN [clinic_app];
ALTER ROLE db_datareader ADD MEMBER [clinic_app];
ALTER ROLE db_datawriter ADD MEMBER [clinic_app];
GRANT EXECUTE TO [clinic_app];
-- Audit log is append-only for the application: it may insert and read, never change history.
DENY UPDATE, DELETE ON dbo.AuditLogs TO [clinic_app];

CREATE LOGIN [clinic_migrator] WITH PASSWORD = '$(MigratorPassword)', CHECK_POLICY = ON;
CREATE USER [clinic_migrator] FOR LOGIN [clinic_migrator];
ALTER ROLE db_ddladmin ADD MEMBER [clinic_migrator];
ALTER ROLE db_datareader ADD MEMBER [clinic_migrator];
ALTER ROLE db_datawriter ADD MEMBER [clinic_migrator];
