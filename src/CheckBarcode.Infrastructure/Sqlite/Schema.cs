namespace CheckBarcode.Infrastructure.Sqlite;

/// <summary>Versioned schema migrations (SQLite ≥ 3.21 syntax only). Never edit a released step: add a new one.</summary>
public static class Schema
{
    private static readonly string[][] Steps =
    {
        // v1 — initial schema
        new[]
        {
            @"CREATE TABLE Role(Code TEXT PRIMARY KEY, NameVi TEXT NOT NULL, NameEn TEXT NOT NULL, IsSystem INTEGER NOT NULL DEFAULT 0)",
            @"CREATE TABLE RolePermission(RoleCode TEXT NOT NULL REFERENCES Role(Code), Permission TEXT NOT NULL, PRIMARY KEY(RoleCode, Permission))",
            @"CREATE TABLE User(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                FullName TEXT NOT NULL DEFAULT '',
                RoleCode TEXT NOT NULL REFERENCES Role(Code),
                Status TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                PasswordSalt TEXT NOT NULL,
                PasswordChangedUtc TEXT NOT NULL,
                MustChangePassword INTEGER NOT NULL DEFAULT 1,
                FailedCount INTEGER NOT NULL DEFAULT 0,
                LockedUtc TEXT NULL,
                LockReason TEXT NOT NULL DEFAULT 'None',
                CreatedUtc TEXT NOT NULL,
                CreatedBy TEXT NOT NULL DEFAULT '')",
            @"CREATE TABLE PasswordHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL REFERENCES User(Id), Hash TEXT NOT NULL, Salt TEXT NOT NULL, CreatedUtc TEXT NOT NULL)",
            @"CREATE TABLE Recipe(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Type TEXT NOT NULL,
                Barcode TEXT NOT NULL,
                BarcodeFormat TEXT NOT NULL,
                SpeedSetpoint REAL NULL, SpeedTolerance REAL NULL,
                TempSetpoint REAL NULL, TempTolerance REAL NULL,
                Version INTEGER NOT NULL DEFAULT 1,
                Status TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL)",
            @"CREATE UNIQUE INDEX UX_Recipe_ActiveName ON Recipe(Name COLLATE NOCASE, Type) WHERE Status = 'Active'",
            @"CREATE TABLE Batch(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BatchNo TEXT NOT NULL UNIQUE COLLATE NOCASE,
                ProductName TEXT NOT NULL,
                BoxRecipeId INTEGER NULL REFERENCES Recipe(Id), BoxRecipeVersion INTEGER NULL,
                LeafletRecipeId INTEGER NULL REFERENCES Recipe(Id), LeafletRecipeVersion INTEGER NULL,
                Status TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL, StartUtc TEXT NULL, EndUtc TEXT NULL,
                CreatedBy TEXT NOT NULL, EndedBy TEXT NULL)",
            @"CREATE TABLE BatchOperator(Id INTEGER PRIMARY KEY AUTOINCREMENT, BatchId INTEGER NOT NULL REFERENCES Batch(Id), Username TEXT NOT NULL, FromUtc TEXT NOT NULL, ToUtc TEXT NULL)",
            @"CREATE TABLE BatchCounter(BatchId INTEGER NOT NULL REFERENCES Batch(Id), Camera TEXT NOT NULL,
                Scanned INTEGER NOT NULL DEFAULT 0, Pass INTEGER NOT NULL DEFAULT 0, Fail INTEGER NOT NULL DEFAULT 0, NoRead INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(BatchId, Camera))",
            @"CREATE TABLE InspectionResult(Id INTEGER PRIMARY KEY AUTOINCREMENT, BatchId INTEGER NOT NULL REFERENCES Batch(Id), Camera TEXT NOT NULL,
                Utc TEXT NOT NULL, ReadString TEXT NOT NULL, Outcome TEXT NOT NULL, ImagePath TEXT NULL)",
            @"CREATE INDEX IX_InspectionResult_Batch ON InspectionResult(BatchId, Camera)",
            @"CREATE INDEX IX_InspectionResult_Utc ON InspectionResult(Utc)",
            @"CREATE TABLE AuditTrail(
                Seq INTEGER PRIMARY KEY,
                Utc TEXT NOT NULL, Username TEXT NOT NULL, Role TEXT NOT NULL, SessionState TEXT NOT NULL,
                ActionCode TEXT NOT NULL, ObjectType TEXT NOT NULL, ObjectId TEXT NOT NULL, Field TEXT NOT NULL,
                OldValue TEXT NOT NULL, NewValue TEXT NOT NULL, Reason TEXT NOT NULL, MessageArgs TEXT NOT NULL,
                BatchId INTEGER NULL, Workstation TEXT NOT NULL, PrevHash TEXT NOT NULL, Hash TEXT NOT NULL)",
            @"CREATE INDEX IX_AuditTrail_Utc ON AuditTrail(Utc)",
            @"CREATE INDEX IX_AuditTrail_Batch ON AuditTrail(BatchId)",
            @"CREATE TABLE ESignature(Id INTEGER PRIMARY KEY AUTOINCREMENT, AuditSeq INTEGER NOT NULL REFERENCES AuditTrail(Seq), Username TEXT NOT NULL, Meaning TEXT NOT NULL, Utc TEXT NOT NULL, Hash TEXT NOT NULL)",
            @"CREATE TABLE AlarmEvent(
                Seq INTEGER PRIMARY KEY,
                AlarmId TEXT NOT NULL, Severity TEXT NOT NULL, State TEXT NOT NULL, Utc TEXT NOT NULL,
                Username TEXT NOT NULL, BatchId INTEGER NULL, PrevHash TEXT NOT NULL, Hash TEXT NOT NULL)",
            @"CREATE INDEX IX_AlarmEvent_Utc ON AlarmEvent(Utc)",
            @"CREATE TABLE EventLog(Id INTEGER PRIMARY KEY AUTOINCREMENT, Utc TEXT NOT NULL, Source TEXT NOT NULL, Code TEXT NOT NULL, Data TEXT NOT NULL)",
            @"CREATE INDEX IX_EventLog_Utc ON EventLog(Utc)",
            @"CREATE TABLE Setting(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)",
            @"CREATE TABLE ReportFile(Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, BatchId INTEGER NULL, Path TEXT NOT NULL, Sha256 TEXT NOT NULL, CreatedUtc TEXT NOT NULL, CreatedBy TEXT NOT NULL)",
            // Append-only enforcement (AUD-01). Even a direct SQL client cannot UPDATE/DELETE without dropping the trigger,
            // and dropping it does not hide edits: the hash chain detects them (AUD-03).
            @"CREATE TRIGGER TR_AuditTrail_NoUpdate BEFORE UPDATE ON AuditTrail BEGIN SELECT RAISE(ABORT, 'AuditTrail is append-only'); END",
            @"CREATE TRIGGER TR_AuditTrail_NoDelete BEFORE DELETE ON AuditTrail BEGIN SELECT RAISE(ABORT, 'AuditTrail is append-only'); END",
            @"CREATE TRIGGER TR_AlarmEvent_NoUpdate BEFORE UPDATE ON AlarmEvent BEGIN SELECT RAISE(ABORT, 'AlarmEvent is append-only'); END",
            @"CREATE TRIGGER TR_AlarmEvent_NoDelete BEFORE DELETE ON AlarmEvent BEGIN SELECT RAISE(ABORT, 'AlarmEvent is append-only'); END",
            @"CREATE TRIGGER TR_ESignature_NoUpdate BEFORE UPDATE ON ESignature BEGIN SELECT RAISE(ABORT, 'ESignature is append-only'); END",
            @"CREATE TRIGGER TR_ESignature_NoDelete BEFORE DELETE ON ESignature BEGIN SELECT RAISE(ABORT, 'ESignature is append-only'); END",
        },
    };

    public static int LatestVersion => Steps.Length;

    /// <summary>Applies all pending steps inside one transaction each. Returns the resulting version.</summary>
    public static int Migrate(SqliteDatabase db)
    {
        db.Execute("CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL)");
        var current = (int)db.ScalarLong("SELECT IFNULL(MAX(Version), 0) FROM SchemaVersion");
        for (var v = current; v < Steps.Length; v++)
        {
            var step = Steps[v];
            var version = v + 1;
            db.InTransaction(() =>
            {
                foreach (var sql in step) db.Execute(sql);
                db.Execute("INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES(?, ?)", version, DateTime.UtcNow);
            });
        }
        return Steps.Length;
    }
}
