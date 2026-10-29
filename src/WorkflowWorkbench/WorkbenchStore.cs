using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WorkflowWorkbench;

public sealed class WorkbenchStore
{
    private const int ApplicationId = 0x57464C57;
    private readonly string connectionString;

    public WorkbenchStore(string path)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        connectionString = new SqliteConnectionStringBuilder
        { DataSource = absolute, ForeignKeys = true, Pooling = false, DefaultTimeout = 3 }.ToString();
        using var db = Open();
        var applicationId = Convert.ToInt32(Scalar(db, null, "PRAGMA application_id;"));
        var format = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version;"));
        var tables = Convert.ToInt32(Scalar(db, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"));
        if ((applicationId != 0 && applicationId != ApplicationId) || (applicationId == 0 && tables > 0)
            || (applicationId == ApplicationId && format != 1))
            throw new InvalidOperationException("This database is not a supported Workflow Workbench database. Choose a new path.");
        using var transaction = db.BeginTransaction(deferred: false);
        Execute(db, transaction, """
            CREATE TABLE IF NOT EXISTS Versions(
              version INTEGER PRIMARY KEY, definition TEXT NOT NULL, published_at TEXT NOT NULL, published_by TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Runs(
              id TEXT PRIMARY KEY, title TEXT NOT NULL, definition_version INTEGER NOT NULL REFERENCES Versions(version),
              revision INTEGER NOT NULL, cycle INTEGER NOT NULL, states TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS History(
              id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES Runs(id), at TEXT NOT NULL,
              actor_id TEXT NOT NULL, actor_name TEXT NOT NULL, action TEXT NOT NULL, step_id TEXT NOT NULL,
              cycle INTEGER NOT NULL, comment TEXT NOT NULL, confirmed INTEGER,
              command_id TEXT NOT NULL, payload_hash TEXT NOT NULL, UNIQUE(run_id, command_id));
            """);
        Execute(db, transaction, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
        if (Convert.ToInt32(Scalar(db, transaction, "SELECT count(*) FROM Versions;")) == 0)
        {
            var definition = Engine.Example();
            Engine.Validate(definition);
            Execute(db, transaction, "INSERT INTO Versions VALUES(1, $definition, $at, 'sample setup');",
                ("$definition", Serialize(definition)), ("$at", Now()));
        }
        transaction.Commit();
    }

    public PublishedVersion[] Versions()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT version,definition,published_at,published_by FROM Versions ORDER BY version DESC LIMIT 50;");
        using var reader = command.ExecuteReader();
        var result = new List<PublishedVersion>();
        while (reader.Read()) result.Add(new(reader.GetInt32(0), Deserialize<WorkflowDefinition>(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        return result.ToArray();
    }

    public PublishedVersion Publish(PublishCommand request, DemoActor actor)
    {
        if (actor.Role != "coordinator") throw new WorkbenchException(403, "Only the coordinator can publish definitions.");
        Engine.Validate(request.Definition);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var current = Convert.ToInt32(Scalar(db, transaction, "SELECT MAX(version) FROM Versions;"));
        if (current != request.ExpectedVersion) throw new WorkbenchException(409, "A newer version was published. Load the current version before publishing your draft.");
        var next = current + 1;
        var at = Now();
        Execute(db, transaction, "INSERT INTO Versions VALUES($version,$definition,$at,$by);",
            ("$version", next), ("$definition", Serialize(request.Definition)), ("$at", at), ("$by", actor.Name));
        transaction.Commit();
        return new(next, request.Definition, at, actor.Name);
    }

    public RunView Create(string title, DemoActor actor)
    {
        if (actor.Role != "requester") throw new WorkbenchException(403, "Choose the Requester demo identity to create a request.");
        Engine.Text(title, "Request title", 100);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var version = Convert.ToInt32(Scalar(db, transaction, "SELECT MAX(version) FROM Versions;"));
        var definition = Deserialize<WorkflowDefinition>((string)Scalar(db, transaction, "SELECT definition FROM Versions WHERE version=$version;", ("$version", version))!);
        var id = Guid.NewGuid().ToString("D");
        Execute(db, transaction, "INSERT INTO Runs VALUES($id,$title,$version,0,1,$states);",
            ("$id", id), ("$title", title.Trim()), ("$version", version), ("$states", Serialize(Engine.Start(definition))));
        AddHistory(db, transaction, id, actor, "created", "", 1, "New request", null, "created", "");
        var result = Load(db, transaction, id);
        transaction.Commit();
        return result;
    }

    public RunSummary[] Runs()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,title,definition_version,revision,cycle,states FROM Runs ORDER BY rowid DESC LIMIT 50;");
        using var reader = command.ExecuteReader();
        var result = new List<RunSummary>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            Engine.Status(Deserialize<Dictionary<string, string>>(reader.GetString(5)))));
        return result.ToArray();
    }

    public RunView Get(string id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var result = Load(db, transaction, id);
        transaction.Commit();
        return result;
    }

    public ActionResult Apply(string id, ActionCommand request, DemoActor actor)
    {
        if (!Guid.TryParseExact(request.CommandId, "D", out var commandId) || commandId == Guid.Empty)
            throw new WorkbenchException(400, "A nonempty UUID command ID is required.");
        Engine.Text(request.Comment, "Comment", 500, required: false, multiline: true);
        Engine.Text(request.StepId, "Step ID", 32);
        if (request.Action is not ("complete" or "return")) throw new WorkbenchException(400, "Choose Complete or Send back.");
        var normalized = request with { Comment = request.Comment.Trim() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { actor.Id, normalized.Action, normalized.StepId, normalized.Comment, normalized.Confirmed }, Engine.Json)));
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var run = Load(db, transaction, id);
        using (var duplicate = Command(db, transaction, "SELECT payload_hash,actor_id FROM History WHERE run_id=$id AND command_id=$command;",
            ("$id", id), ("$command", commandId.ToString("D"))))
        using (var reader = duplicate.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(1) != actor.Id) throw new WorkbenchException(403, "This command belongs to another actor.");
                if (reader.GetString(0) != hash) throw new WorkbenchException(409, "This command ID was already used for a different action.");
                return new(run, true);
            }
        }
        if (request.ExpectedRevision != run.Revision) throw new WorkbenchException(409, "The request changed in another session. Reload before acting.");
        var states = Engine.Apply(run.Definition, run.Steps, normalized, actor.Role);
        var cycle = run.Cycle + (request.Action == "return" ? 1 : 0);
        Execute(db, transaction, "UPDATE Runs SET revision=revision+1,cycle=$cycle,states=$states WHERE id=$id;",
            ("$cycle", cycle), ("$states", Serialize(states)), ("$id", id));
        AddHistory(db, transaction, id, actor, request.Action, request.StepId, run.Cycle, normalized.Comment,
            normalized.Confirmed, commandId.ToString("D"), hash);
        var result = Load(db, transaction, id);
        transaction.Commit();
        return new(result, false);
    }

    private static RunView Load(SqliteConnection db, SqliteTransaction transaction, string id)
    {
        string title;
        int version, revision, cycle;
        Dictionary<string, string> states;
        WorkflowDefinition definition;
        using (var command = Command(db, transaction, """
            SELECT r.title,r.definition_version,r.revision,r.cycle,r.states,v.definition
            FROM Runs r JOIN Versions v ON v.version=r.definition_version WHERE r.id=$id;
            """, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new WorkbenchException(404, "Request not found.");
            title = reader.GetString(0); version = reader.GetInt32(1); revision = reader.GetInt32(2); cycle = reader.GetInt32(3);
            states = Deserialize<Dictionary<string, string>>(reader.GetString(4));
            definition = Deserialize<WorkflowDefinition>(reader.GetString(5));
        }
        using var historyCommand = Command(db, transaction, "SELECT id,at,actor_name,action,step_id,cycle,comment,confirmed FROM History WHERE run_id=$id ORDER BY id;", ("$id", id));
        using var historyReader = historyCommand.ExecuteReader();
        var history = new List<AuditEntry>();
        while (historyReader.Read()) history.Add(new(historyReader.GetInt64(0), historyReader.GetString(1), historyReader.GetString(2), historyReader.GetString(3),
            historyReader.GetString(4), historyReader.GetInt32(5), historyReader.GetString(6), historyReader.IsDBNull(7) ? null : historyReader.GetBoolean(7)));
        return new(id, title, version, revision, cycle, Engine.Status(states), states, definition, history.ToArray());
    }

    private static void AddHistory(SqliteConnection db, SqliteTransaction transaction, string id, DemoActor actor,
        string action, string stepId, int cycle, string comment, bool? confirmed, string commandId, string hash) =>
        Execute(db, transaction, """
            INSERT INTO History(run_id,at,actor_id,actor_name,action,step_id,cycle,comment,confirmed,command_id,payload_hash)
            VALUES($id,$at,$actor,$name,$action,$step,$cycle,$comment,$confirmed,$command,$hash);
            """, ("$id", id), ("$at", Now()), ("$actor", actor.Id), ("$name", actor.Name), ("$action", action), ("$step", stepId),
            ("$cycle", cycle), ("$comment", comment), ("$confirmed", confirmed), ("$command", commandId), ("$hash", hash));

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    {
        var command = new SqliteCommand(sql, db, transaction);
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    { using var command = Command(db, transaction, sql, values); command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    { using var command = Command(db, transaction, sql, values); return command.ExecuteScalar(); }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Engine.Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Engine.Json)!;
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
