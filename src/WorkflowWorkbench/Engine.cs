using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkflowWorkbench;

public record StepDefinition(string Id, string Name, string Role, string[] Requires, string Question);
public record WorkflowDefinition(string Name, StepDefinition[] Steps);
public record PublishedVersion(int Version, WorkflowDefinition Definition, string PublishedAt, string PublishedBy);
public record DemoActor(string Id, string Name, string Role);
public record ActionCommand(int ExpectedRevision, string CommandId, string StepId, string Action, string Comment, bool? Confirmed);
public record PublishCommand(int ExpectedVersion, WorkflowDefinition Definition);
public record CreateCommand(string Title);
public record SessionCommand(string ActorId);
public record AuditEntry(long Id, string At, string Actor, string Action, string StepId, int Cycle, string Comment, bool? Confirmed);
public record RunView(string Id, string Title, int DefinitionVersion, int Revision, int Cycle,
    string Status, Dictionary<string, string> Steps, WorkflowDefinition Definition, AuditEntry[] History);
public record RunSummary(string Id, string Title, int DefinitionVersion, int Revision, int Cycle, string Status);
public record ActionResult(RunView Run, bool Replayed);

public sealed class WorkbenchException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static partial class Engine
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    public static readonly DemoActor[] Actors =
    [
        new("casey", "Casey — Coordinator", "coordinator"),
        new("alex", "Alex — Requester", "requester"),
        new("morgan", "Morgan — Operations", "operations"),
        new("sam", "Sam — Finance", "finance"),
        new("taylor", "Taylor — Technical", "technical")
    ];
    public static WorkflowDefinition Example() => new("Equipment request",
    [
        new("intake", "Request intake", "requester", [], "Is the request description complete?"),
        new("operations", "Operations review", "operations", ["intake"], "Is the equipment needed for the work?"),
        new("finance", "Finance review", "finance", ["intake"], "Has the budget been checked?"),
        new("release", "Ready to order", "coordinator", ["operations", "finance"], "Are all required reviews ready for release?")
    ]);

    public static void Text(string? text, string name, int maximum, bool required = true, bool multiline = false)
    {
        if (text is null || text.Length > maximum || (required && string.IsNullOrWhiteSpace(text))
            || text.Any(character => char.IsControl(character) && !(multiline && (character is '\r' or '\n'))))
            throw new WorkbenchException(400, $"{name} must be {(required ? "1" : "0")}–{maximum} characters without control characters.");
    }

    public static void Validate(WorkflowDefinition? definition)
    {
        if (definition is null) throw new WorkbenchException(400, "A workflow definition is required.");
        Text(definition.Name, "Workflow name", 80);
        if (definition.Steps is null || definition.Steps.Length is < 2 or > 10)
            throw new WorkbenchException(400, "Use between 2 and 10 steps.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (step is null || step.Id is null || !StepIdPattern().IsMatch(step.Id) || !ids.Add(step.Id))
                throw new WorkbenchException(400, "Step IDs must be unique lowercase words/numbers/hyphens, starting with a letter (32 characters maximum).");
            Text(step.Name, "Step name", 60);
            Text(step.Question, "Confirmation question", 180, required: false);
            if (!Actors.Any(actor => actor.Role == step.Role)) throw new WorkbenchException(400, "Choose a supported role for every step.");
            if (step.Requires is null || step.Requires.Length > 9 || step.Requires.Distinct().Count() != step.Requires.Length)
                throw new WorkbenchException(400, "Prerequisites must be a distinct list of step IDs.");
        }
        if (definition.Steps.Any(step => step.Requires.Any(id => id is null || id == step.Id || !ids.Contains(id))))
            throw new WorkbenchException(400, "Prerequisites must reference other steps in this workflow.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var ready = definition.Steps.Where(step => !visited.Contains(step.Id) && step.Requires.All(visited.Contains)).ToArray();
            if (ready.Length == 0) break;
            foreach (var step in ready) visited.Add(step.Id);
        }
        if (visited.Count != definition.Steps.Length) throw new WorkbenchException(400, "The workflow contains a cycle. Use Send back for rework instead of drawing a cycle.");
        if (definition.Steps.Count(step => step.Requires.Length == 0) != 1)
            throw new WorkbenchException(400, "Use exactly one starting step.");
        var prerequisiteIds = definition.Steps.SelectMany(step => step.Requires).ToHashSet();
        if (definition.Steps.Count(step => !prerequisiteIds.Contains(step.Id)) != 1)
            throw new WorkbenchException(400, "Use exactly one final step; connect every branch to it.");
    }

    public static Dictionary<string, string> Start(WorkflowDefinition definition) => definition.Steps.ToDictionary(
        step => step.Id, step => step.Requires.Length == 0 ? "active" : "waiting");

    public static Dictionary<string, string> Apply(WorkflowDefinition definition,
        IReadOnlyDictionary<string, string> current, ActionCommand command, string role)
    {
        var step = definition.Steps.SingleOrDefault(item => item.Id == command.StepId)
            ?? throw new WorkbenchException(400, "This step does not exist in the request's workflow version.");
        if (step.Role != role) throw new WorkbenchException(403, "Your demo role cannot act on this step.");
        if (!current.TryGetValue(step.Id, out var status) || status != "active")
            throw new WorkbenchException(409, "This step is not active. Complete its prerequisites or reload the request.");
        Text(command.Comment, "Comment", 500, required: command.Action == "return", multiline: true);
        if (command.Action == "return")
        {
            if (step.Requires.Length == 0) throw new WorkbenchException(400, "The intake step is already the starting point.");
            return Start(definition);
        }
        if (command.Action != "complete") throw new WorkbenchException(400, "Choose Complete or Send back.");
        if (step.Question.Length > 0 && command.Confirmed != true)
            throw new WorkbenchException(400, "Confirm the step's question before completing it, or send the request back with a reason.");
        var next = new Dictionary<string, string>(current) { [step.Id] = "complete" };
        foreach (var candidate in definition.Steps)
            if (next[candidate.Id] == "waiting" && candidate.Requires.All(id => next[id] == "complete"))
                next[candidate.Id] = "active";
        return next;
    }

    public static string Status(IReadOnlyDictionary<string, string> states) => states.Values.All(state => state == "complete") ? "complete" : "in progress";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();
}
