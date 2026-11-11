using System.IO.Pipes;
using System.Runtime.InteropServices;
using NaxExplorer.Core;
using NaxStartMenu.Integration;

namespace NaxExplorer.Tests;

internal static class StartMenuPinTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("startmenu_shortcut_registration_preserves_launch_settings", "A desktop link keeps its arguments, working folder and icon when pinned, survives the original's removal, and repeated registration has one stable target.", () => OnSta(() =>
        {
            string root = Fixtures.TempFolder("startmenu-shortcut");
            string source = Path.Combine(root, "Example app.lnk");
            string programs = Path.Combine(root, "Programs");
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object? link = null, registeredLink = null;
            try
            {
                link = ((dynamic)shell).CreateShortcut(source);
                ((dynamic)link).TargetPath = Environment.ProcessPath!;
                ((dynamic)link).Arguments = "--example \"with spaces\"";
                ((dynamic)link).WorkingDirectory = root;
                ((dynamic)link).IconLocation = Environment.ProcessPath! + ",0";
                ((dynamic)link).Save();
                string registered = StartMenuPinning.Register(source, programs);
                Check.Equal(registered, StartMenuPinning.Register(source, programs));
                Check.That(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(registered)), "link bytes must be retained");
                registeredLink = ((dynamic)shell).CreateShortcut(registered);
                Check.Equal("--example \"with spaces\"", (string)((dynamic)registeredLink).Arguments);
                Check.Equal(root, (string)((dynamic)registeredLink).WorkingDirectory);
                Check.Equal(Environment.ProcessPath! + ",0", (string)((dynamic)registeredLink).IconLocation);
                File.Delete(source);
                Check.That(File.Exists(registered), "removing the desktop link must not remove the pin");
                Check.Equal(registered, StartMenuPinning.Register(registered, programs));
            }
            finally
            {
                if (registeredLink is not null) Marshal.FinalReleaseComObject(registeredLink);
                if (link is not null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }));

        yield return ("startmenu_registration_separates_same_named_apps", "Two shortcuts with the same display name do not overwrite each other, and documents are not offered as applications.", () =>
        {
            string root = Fixtures.TempFolder("startmenu-same-names");
            string a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), programs = Path.Combine(root, "Programs");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            string first = Path.Combine(a, "App.lnk"), second = Path.Combine(b, "App.lnk");
            File.WriteAllText(first, "first link"); File.WriteAllText(second, "second link");
            string registeredA = StartMenuPinning.Register(first, programs), registeredB = StartMenuPinning.Register(second, programs);
            Check.That(registeredA != registeredB, "same names must not collide");
            Check.Equal("first link", File.ReadAllText(registeredA));
            Check.Equal("second link", File.ReadAllText(registeredB));
            Check.That(!StartMenuPinning.CanPin(Path.Combine(root, "notes.txt")), "documents excluded");
            Check.That(!StartMenuPinning.CanPin("relative.exe"), "relative paths excluded");
        });

        yield return ("startmenu_registration_handles_executables_and_game_links", "A standalone executable gets a shortcut with its own working directory; an Internet/game link retains its full original contents.", () => OnSta(() =>
        {
            string root = Fixtures.TempFolder("startmenu-executable");
            string programs = Path.Combine(root, "Programs");
            string registered = StartMenuPinning.Register(Environment.ProcessPath!, programs);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object? shortcut = null;
            try
            {
                shortcut = ((dynamic)shell).CreateShortcut(registered);
                Check.Equal(Environment.ProcessPath!, (string)((dynamic)shortcut).TargetPath);
                Check.Equal(Path.GetDirectoryName(Environment.ProcessPath!)!, (string)((dynamic)shortcut).WorkingDirectory);
            }
            finally
            {
                if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
            string game = Path.Combine(root, "Game.url");
            const string contents = "[InternetShortcut]\r\nURL=steam://rungameid/123\r\nIconIndex=0\r\n";
            File.WriteAllText(game, contents);
            Check.Equal(contents, File.ReadAllText(StartMenuPinning.Register(game, programs)));
        }));

        yield return ("startmenu_pin_pipe_recovers_from_bad_and_interrupted_clients", "The real pipe round-trips group names and pin requests, rejects unknown protocol versions, and continues after malformed and disconnected clients.", () =>
        {
            string name = "NaxPinTest." + Guid.NewGuid().ToString("N");
            int handled = 0;
            var errors = new List<Exception>();
            using var server = new StartMenuPinServer((request, _) =>
            {
                Interlocked.Increment(ref handled);
                return Task.FromResult(request.Operation == "groups"
                    ? new PinReply(true, Groups: [new("games", "Spiele & Spaß")])
                    : new PinReply(request.AppId == @"C:\Programs\App.lnk" && request.GroupId == "games"));
            }, ex => { lock (errors) errors.Add(ex); }, name);
            PinReply groups = StartMenuProtocol.SendAsync(new("groups"), name).GetAwaiter().GetResult();
            Check.Equal("Spiele & Spaß", groups.Groups!.Single().Name);
            Check.That(StartMenuProtocol.SendAsync(new("pin", @"C:\Programs\App.lnk", "games"), name).GetAwaiter().GetResult().Success, "pin acknowledgement");
            Check.That(!StartMenuProtocol.SendAsync(new("pin", Version: 99), name).GetAwaiter().GetResult().Success, "version rejected");
            using (var malformed = new NamedPipeClientStream(".", name, PipeDirection.InOut))
            {
                malformed.Connect(2000);
                malformed.Write(BitConverter.GetBytes(StartMenuProtocol.MaximumMessageBytes + 1));
                malformed.Flush();
                try { malformed.ReadByte(); } catch (IOException) { }
            }
            Check.That(StartMenuProtocol.SendAsync(new("groups"), name).GetAwaiter().GetResult().Success, "valid request after malformed frame");
            using (var interrupted = new NamedPipeClientStream(".", name, PipeDirection.InOut))
            {
                interrupted.Connect(2000);
                interrupted.Write(new byte[] { 20, 0 });
            }
            Check.That(StartMenuProtocol.SendAsync(new("groups"), name).GetAwaiter().GetResult().Success, "valid request after interrupted frame");
            Check.Equal(4, handled, "invalid frames/versions must never reach handler");
            server.Dispose();
            Check.That(server.Completion.Wait(2000), "shutdown must release the listener");
        });

        yield return ("startmenu_pin_listener_answers_a_failing_request_and_keeps_listening", "A handler that throws an unexpected exception turns that one request into an error reply and a reported error, and the next request is still answered.", () =>
        {
            string name = "NaxPinTest." + Guid.NewGuid().ToString("N");
            var errors = new List<Exception>();
            using var server = new StartMenuPinServer(async (request, _) =>
            {
                await Task.Yield();
                if (request.AppId == "broken") throw new InvalidOperationException("layout is busy");
                return new PinReply(true, Groups: [new("tools", "Tools")]);
            }, ex => { lock (errors) errors.Add(ex); }, name);
            PinReply failed = StartMenuProtocol.SendAsync(new("pin", "broken", "tools"), name).GetAwaiter().GetResult();
            Check.That(!failed.Success && failed.Error is { } error && error.Contains("layout is busy"), "a failing request did not get an error reply");
            Check.That(StartMenuProtocol.SendAsync(new("groups"), name).GetAwaiter().GetResult().Success, "the listener stopped after a failing request");
            lock (errors) Check.That(errors.Any(ex => ex is InvalidOperationException), "the failure was not reported");
            Check.That(!server.Completion.IsCompleted, "the listener ended");
        });
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var worker = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        if (error is not null) throw error;
    }
}
