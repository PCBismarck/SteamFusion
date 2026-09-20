using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SteamFusion.Windows;

internal static class PipeIdentityTests
{
    public static async Task<int> Run()
    {
        var name = "SteamFusion-test-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        using var server = Ipc.CreateServer(name);
        var security = server.GetAccessControl();
        if (!identity.User!.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new Exception("Pipe ownership/access inheritance is not restricted to the actual user.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        if (rules.Length != 1 || !identity.User.Equals(rules[0].IdentityReference) || rules[0].AccessControlType != AccessControlType.Allow)
            throw new Exception("Pipe grants access to another identity.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () => {
            await server.WaitForConnectionAsync(timeout.Token);
            var input = new byte[1];
            if (await server.ReadAsync(input, timeout.Token) != 1 || input[0] != 42) throw new Exception("Missing handshake.");
            Ipc.ValidateClientUser(server);
            await server.WriteAsync(new byte[] { 43 }, timeout.Token);
        });
        using var client = Ipc.CreateClient(name);
        await client.ConnectAsync(3000, timeout.Token);
        Ipc.ValidateServerOwner(client);
        await client.WriteAsync(new byte[] { 42 }, timeout.Token);
        var response = new byte[1];
        if (await client.ReadAsync(response, timeout.Token) != 1 || response[0] != 43) throw new Exception("Missing authenticated reply.");
        await serving;
        using var anonymousServer = Ipc.CreateServer(name + "-anonymous");
        var rejecting = Task.Run(async () => {
            await anonymousServer.WaitForConnectionAsync(timeout.Token);
            var input = new byte[1]; await anonymousServer.ReadExactlyAsync(input, timeout.Token);
            try { Ipc.ValidateClientUser(anonymousServer); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }
            throw new Exception("An unidentified client passed authentication.");
        });
        using var anonymousClient = new NamedPipeClientStream(".", name + "-anonymous", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await anonymousClient.ConnectAsync(3000, timeout.Token);
        await anonymousClient.WriteAsync(new byte[] { 42 }, timeout.Token);
        await rejecting;
        Console.WriteLine("PASS pipe identity: protected user-only ACL, actual user ownership, server owner validation, client identification, anonymous-client rejection and round-trip response.");
        return 0;
    }
}
