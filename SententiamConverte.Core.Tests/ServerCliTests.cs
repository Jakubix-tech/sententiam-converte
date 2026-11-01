using SententiamConverte.Server;

namespace SententiamConverte.Core.Tests;

public class ServerCliTests
{
    [Fact]
    public void DataFolderMayPrecedeTheCommand()
    {
        // so steht es in LIESMICH und Handbuch: „--daten ./teamdaten benutzer neu Anna bearbeiter“
        Assert.Equal(["benutzer", "neu", "Anna", "bearbeiter"], ServerCli.CommandArgs(["--daten", "./teamdaten", "benutzer", "neu", "Anna", "bearbeiter"])!);
        Assert.Equal(["benutzer", "liste"], ServerCli.CommandArgs(["benutzer", "liste", "--daten", "./teamdaten"])!);
        Assert.Null(ServerCli.CommandArgs(["--daten", "./teamdaten"]));
        Assert.Null(ServerCli.CommandArgs(["--daten", "./teamdaten", "--urls", "http://0.0.0.0:5300"]));
        Assert.Null(ServerCli.CommandArgs([]));
    }

    [Fact]
    public void CreatesUserInTheGivenFolder()
    {
        using var dir = new TempDir();
        var command = ServerCli.CommandArgs(["--daten", dir.Path, "benutzer", "neu", "Anna", "leser"])!;
        Assert.Equal(0, ServerCli.Run(command, dir.Path));
        Assert.Contains(new UserStore(dir.Path).All, u => u.Name == "Anna");
    }
}
