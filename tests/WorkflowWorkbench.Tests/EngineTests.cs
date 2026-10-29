using System.Text.Json;
using WorkflowWorkbench;
using Xunit;

namespace WorkflowWorkbench.Tests;

public class EngineTests
{
    [Fact]
    public void ExampleHasOneStartAndTwoParallelReviews()
    {
        var definition = Engine.Example();
        Engine.Validate(definition);
        var initial = Engine.Start(definition);
        Assert.Equal("active", initial["intake"]);
        var next = Engine.Apply(definition, initial, Command("intake"), "requester");
        Assert.Equal("active", next["operations"]);
        Assert.Equal("active", next["finance"]);
        Assert.Equal("waiting", next["release"]);
    }

    [Theory]
    [InlineData("operations", "finance")]
    [InlineData("finance", "operations")]
    public void JoinWaitsForBothBranchesInEitherCompletionOrder(string first, string second)
    {
        var definition = Engine.Example();
        var started = Engine.Apply(definition, Engine.Start(definition), Command("intake"), "requester");
        var half = Engine.Apply(definition, started, Command(first), first);
        Assert.Equal("waiting", half["release"]);
        var ready = Engine.Apply(definition, half, Command(second), second);
        Assert.Equal("active", ready["release"]);
        var done = Engine.Apply(definition, ready, Command("release"), "coordinator");
        Assert.Equal("complete", Engine.Status(done));
    }

    [Fact]
    public void WrongRoleUnansweredQuestionAndWaitingStepAreRejected()
    {
        var definition = Engine.Example();
        var start = Engine.Start(definition);
        Assert.Equal(403, Assert.Throws<WorkbenchException>(() => Engine.Apply(definition, start, Command("intake"), "finance")).Status);
        Assert.Equal(400, Assert.Throws<WorkbenchException>(() => Engine.Apply(definition, start, Command("intake") with { Confirmed = false }, "requester")).Status);
        Assert.Equal(409, Assert.Throws<WorkbenchException>(() => Engine.Apply(definition, start, Command("release"), "coordinator")).Status);
        Assert.Equal("active", start["intake"]);
    }

    [Fact]
    public void ReturnRequiresAReasonAndRestartsEveryBranch()
    {
        var definition = Engine.Example();
        var states = Engine.Apply(definition, Engine.Start(definition), Command("intake"), "requester");
        states = Engine.Apply(definition, states, Command("operations"), "operations");
        var sendBack = Command("finance") with { Action = "return" };
        Assert.Throws<WorkbenchException>(() => Engine.Apply(definition, states, sendBack, "finance"));
        var reset = Engine.Apply(definition, states, sendBack with { Comment = "Need a quote\nInclude delivery." }, "finance");
        Assert.Equal("active", reset["intake"]);
        Assert.All(reset.Where(pair => pair.Key != "intake"), pair => Assert.Equal("waiting", pair.Value));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("role")]
    [InlineData("id")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("roots")]
    [InlineData("ends")]
    public void InvalidDefinitionsCannotBePublished(string problem)
    {
        var definition = Engine.Example();
        var steps = definition.Steps;
        switch (problem)
        {
            case "name": definition = definition with { Name = " " }; break;
            case "role": steps[0] = steps[0] with { Role = "unknown" }; break;
            case "id": steps[0] = steps[0] with { Id = "../bad" }; break;
            case "duplicate": steps[1] = steps[1] with { Id = steps[0].Id }; break;
            case "unknown": steps[1] = steps[1] with { Requires = ["missing"] }; break;
            case "self": steps[1] = steps[1] with { Requires = [steps[1].Id] }; break;
            case "cycle": steps[1] = steps[1] with { Requires = ["release"] }; break;
            case "roots": steps[1] = steps[1] with { Requires = [] }; break;
            case "ends": steps[3] = steps[3] with { Requires = ["finance"] }; break;
        }
        Assert.Throws<WorkbenchException>(() => Engine.Validate(definition));
    }

    [Fact]
    public void FieldBoundsNullsAndDuplicatePrerequisitesAreRejected()
    {
        Assert.Throws<WorkbenchException>(() => Engine.Validate(null));
        Assert.Throws<WorkbenchException>(() => Engine.Validate(new("Example", [])));
        var definition = Engine.Example();
        definition.Steps[1] = definition.Steps[1] with { Requires = ["intake", "intake"] };
        Assert.Throws<WorkbenchException>(() => Engine.Validate(definition));
        Assert.Throws<WorkbenchException>(() => Engine.Text("bad\0name", "Name", 30));
        Assert.Throws<WorkbenchException>(() => Engine.Text(new string('x', 101), "Title", 100));
        Engine.Text("line one\nline two", "Comment", 500, multiline: true);
    }

    [Fact]
    public void JsonContractRejectsMissingAndUnknownProperties()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCommand>("{}", Engine.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCommand>("{\"title\":\"Printer\",\"role\":\"coordinator\"}", Engine.Json));
    }

    private static ActionCommand Command(string step) => new(0, Guid.NewGuid().ToString("D"), step, "complete", "", true);
}
