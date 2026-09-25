using System.Buffers.Binary;
using System.Text;
using ForgeDesk.App.Activation;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class ActivationRequestTests
{
    private static readonly string WorkingDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forgedesk-activation"));

    [Fact]
    public void No_arguments_is_empty() =>
        ActivationRequest.Parse([], WorkingDirectory).IsEmpty.Should().BeTrue();

    [Fact]
    public void Open_project_option_takes_the_next_argument()
    {
        var request = ActivationRequest.Parse(["--open-project", "0198c1f2a3b44c5d8e9f001122334455"], WorkingDirectory);

        request.ProjectId.Should().Be("0198c1f2a3b44c5d8e9f001122334455");
        request.FolderPath.Should().BeNull();
        request.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Open_project_option_accepts_equals_syntax_and_any_case() =>
        ActivationRequest.Parse(["--Open-Project=abc123"], WorkingDirectory).ProjectId.Should().Be("abc123");

    [Fact]
    public void Open_project_without_value_is_ignored() =>
        ActivationRequest.Parse(["--open-project"], WorkingDirectory).IsEmpty.Should().BeTrue();

    [Fact]
    public void Open_project_does_not_swallow_a_following_switch() =>
        ActivationRequest.Parse(["--open-project", "-ToastActivated"], WorkingDirectory).ProjectId.Should().BeNull();

    [Fact]
    public void Relative_folder_resolves_against_the_callers_directory()
    {
        var request = ActivationRequest.Parse(["."], WorkingDirectory);

        request.FolderPath.Should().Be(WorkingDirectory);
    }

    [Fact]
    public void Folder_path_is_normalized()
    {
        var folder = Path.Combine(WorkingDirectory, "repo") + Path.DirectorySeparatorChar;

        ActivationRequest.Parse([folder], "/").FolderPath.Should().Be(Path.Combine(WorkingDirectory, "repo"));
    }

    [Fact]
    public void Windows_activation_switches_are_ignored() =>
        ActivationRequest.Parse(["-ToastActivated", "-Embedding"], WorkingDirectory).IsEmpty.Should().BeTrue();

    [Fact]
    public void Project_and_folder_can_be_combined_and_first_wins()
    {
        var request = ActivationRequest.Parse(["repo", "--open-project", "one", "other", "--open-project", "two"], WorkingDirectory);

        request.ProjectId.Should().Be("one");
        request.FolderPath.Should().Be(Path.Combine(WorkingDirectory, "repo"));
        request.Arguments.Should().HaveCount(6);
    }

    [Fact]
    public void For_project_round_trips_through_parse()
    {
        var request = ActivationRequest.ForProject("p1");

        ActivationRequest.Parse(request.Arguments, WorkingDirectory).ProjectId.Should().Be("p1");
    }
}

public class ActivationMessageTests
{
    [Fact]
    public async Task Message_round_trips_through_a_stream()
    {
        var token = TestContext.Current.CancellationToken;
        var original = new ActivationMessage(["--open-project", "id with \"quotes\"", "C:\\dev\\é"], "/work/dir");
        using var stream = new MemoryStream();

        await ActivationMessage.WriteAsync(stream, original, token);
        stream.Position = 0;
        var read = await ActivationMessage.ReadAsync(stream, token);

        read.Should().NotBeNull();
        read!.Arguments.Should().Equal(original.Arguments);
        read.WorkingDirectory.Should().Be("/work/dir");
    }

    [Fact]
    public async Task Truncated_payload_is_rejected()
    {
        using var stream = new MemoryStream();
        await ActivationMessage.WriteAsync(stream, new ActivationMessage(["a", "b"], "/"), TestContext.Current.CancellationToken);
        using var truncated = new MemoryStream(stream.ToArray()[..^3]);

        (await ActivationMessage.ReadAsync(truncated, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ActivationMessage.MaxPayloadBytes + 1)]
    public async Task Invalid_lengths_are_rejected_without_reading(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        (await ActivationMessage.ReadAsync(stream, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var payload = Encoding.UTF8.GetBytes("{not json");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        using var stream = new MemoryStream([.. header, .. payload]);

        (await ActivationMessage.ReadAsync(stream, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Empty_stream_is_rejected() =>
        (await ActivationMessage.ReadAsync(new MemoryStream(), TestContext.Current.CancellationToken)).Should().BeNull();
}

public class ActivationPipeTests
{
    private static string UniquePipeName() => "ForgeDesk.Tests." + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Second_instance_message_reaches_the_primary()
    {
        var token = TestContext.Current.CancellationToken;
        var pipeName = UniquePipeName();
        var received = new TaskCompletionSource<ActivationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ActivationPipeServer(pipeName, message =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        });
        server.Start();

        var sent = await ActivationPipeClient.TrySendAsync(pipeName, new ActivationMessage(["--open-project", "abc"], "/tmp"),
            TimeSpan.FromSeconds(1.5), cancellationToken: token);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(1.5), token);

        sent.Should().BeTrue();
        message.Arguments.Should().Equal("--open-project", "abc");
        message.WorkingDirectory.Should().Be("/tmp");
    }

    [Fact]
    public async Task Server_keeps_listening_after_a_message()
    {
        var token = TestContext.Current.CancellationToken;
        var pipeName = UniquePipeName();
        var count = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ActivationPipeServer(pipeName, _ =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                both.TrySetResult();
            }

            return Task.CompletedTask;
        });
        server.Start();

        (await ActivationPipeClient.TrySendAsync(pipeName, new ActivationMessage(["one"], "/"), TimeSpan.FromSeconds(1.5), cancellationToken: token)).Should().BeTrue();
        (await ActivationPipeClient.TrySendAsync(pipeName, new ActivationMessage(["two"], "/"), TimeSpan.FromSeconds(1.5), cancellationToken: token)).Should().BeTrue();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(1.5), token);

        count.Should().Be(2);
    }

    [Fact]
    public async Task Client_gives_up_when_no_primary_is_listening()
    {
        var sent = await ActivationPipeClient.TrySendAsync(UniquePipeName(), new ActivationMessage([], "/"),
            TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
    }
}

public class InstanceIdentityTests
{
    [Fact]
    public void Names_are_scoped_to_the_user()
    {
        var identity = InstanceIdentity.Create("S-1-5-21-1004336348-1177238915-682003330-1001", null);

        identity.MutexName.Should().Be("ForgeDesk.SingleInstance.S-1-5-21-1004336348-1177238915-682003330-1001");
        identity.PipeName.Should().Be("ForgeDesk.Activation.S-1-5-21-1004336348-1177238915-682003330-1001");
    }

    [Fact]
    public void Unsafe_characters_are_replaced() =>
        InstanceIdentity.Create(@"DOMAIN\jo hn/x", null).MutexName.Should().Be("ForgeDesk.SingleInstance.DOMAIN_jo_hn_x");

    [Fact]
    public void Custom_data_folder_gets_its_own_instance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "forgedesk-portable");
        var normal = InstanceIdentity.Create("user", null);
        var portable = InstanceIdentity.Create("user", folder);

        portable.MutexName.Should().NotBe(normal.MutexName).And.StartWith(normal.MutexName + ".");
        portable.PipeName.Should().NotBe(normal.PipeName);
        InstanceIdentity.Create("user", folder + Path.DirectorySeparatorChar).Should().Be(portable);
        InstanceIdentity.Create("user", Path.Combine(Path.GetTempPath(), "other")).Should().NotBe(portable);
    }
}

public class SingleInstanceGuardTests
{
    [Fact]
    public void Only_the_first_holder_is_primary()
    {
        var name = "ForgeDesk.Tests.Guard." + Guid.NewGuid().ToString("N")[..12];
        using var first = SingleInstanceGuard.Acquire(name);

        bool secondIsPrimary = true;
        var other = new Thread(() =>
        {
            using var second = SingleInstanceGuard.Acquire(name);
            secondIsPrimary = second.IsPrimary;
        });
        other.Start();
        other.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();

        first.IsPrimary.Should().BeTrue();
        secondIsPrimary.Should().BeFalse();
    }

    [Fact]
    public void Released_guard_lets_the_next_instance_become_primary()
    {
        var name = "ForgeDesk.Tests.Guard." + Guid.NewGuid().ToString("N")[..12];
        SingleInstanceGuard.Acquire(name).Dispose();

        bool isPrimary = false;
        var other = new Thread(() =>
        {
            using var next = SingleInstanceGuard.Acquire(name);
            isPrimary = next.IsPrimary;
        });
        other.Start();
        other.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();

        isPrimary.Should().BeTrue();
    }
}
