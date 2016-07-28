using System;
using System.IO;
using System.Linq;
using System.Timers;

namespace ToxikkServerLauncher
{
  class CLI
  {
    private const string Version = "2.21";

    [Flags]
    private enum ServerAction { Start = 0x01, Stop = 0x02, Restart = 0x03, Focus = 0x04 }

    private Launcher launcher;
    private Workshop workshop;
    private bool interactive;
    private ServerAction action;
    private readonly Timer reloadTimer = new Timer(100);

    #region Run()
    public void Run(string[] args)
    {
      FileSystemWatcher watcher = null;

      Utils.Write("^FToxikkServerLauncher " + Version + "^7\nhttps://github.com/ToxikkModdingTeam/ToxikkServerLauncher\n");

      if (!LoadMyServerConfigIni())
        return;

      // setup interactive mode when no server IDs were specified on the command line
      var commands = args.ToList();
      if (commands.Count == 0)
      {
        watcher = MonitorChangesToMyServerConfigIni();
        reloadTimer.AutoReset = false;
        reloadTimer.Elapsed += ReloadMyServerConfigIni;
        interactive = true;
        ListConfigurations();
      }

      do
      {
        action = ServerAction.Start;
        if (interactive)
        {
          ShowInteractivePrompt();
          commands = (Console.ReadLine() ?? "").Split(' ').ToList();
          if (commands.Count == 1 && commands[0] == "")
            continue;
          launcher.FindRunningServers();
        }

        foreach (var cmdOrId in commands)
          ProcessCommand(cmdOrId);
      } while (interactive);

      watcher?.Dispose();
    }
    #endregion

    #region ShowHelp()
    private void ShowHelp()
    {
      Utils.Write(@"
ToxikkServerLauncher [command|server-id]...

Multiple commands and server numbers can be mixed, e.g.: stop 1 start 2
The full documentation can be found on https://github.com/PredatH0r/ToxikkServerLauncher

^FBasic commands^7:
  help, -h, -?:        This help screen
  list, -l:            Lists the available servers and their status
  start, -s:           Start servers with the following ids
  restart, -r:         Restart servers with following ids
  stop, -x:            Stop servers with the following ids
  focus, -f:           Focuses the console window of the specified server id
  quit, exit:          Quit the server launcher

^FAdvanced commands^7:
  updateToxikk, -ut:   Update TOXIKK game files with steamcmd
  cleanWorkshop, -cw:  Delete content of the steamcmd workshop\324810 folder
  updateWorkshop, -uw: Update workshop items with steamcmd (implies -syncWorkshop)
  syncWorkshop, -sw:   Copy steamcmd workshop folders to TOXIKK\UDKGame\Workshop
  listen:              Start a listen server instead of a dedicated server
  noSteamSockets:      Don't append ?steamsockets to the launch URL
  noSeekFreeLoading:   Don't append -seekfreeloading to the command line
  showCommand:         Print the generated TOXIKK.exe command line on screen before starting TOXIKK
  verbose, -v:         More log output
  interactive, -i:     Run in interactive command line interface mode

");
    }
    #endregion

    #region MonitorChangesToMyServerConfigIni()
    private FileSystemWatcher MonitorChangesToMyServerConfigIni()
    {
      var watcher = new FileSystemWatcher(Path.GetDirectoryName(this.launcher.MainIni.FileName) ?? "");
      watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
      watcher.Changed += DelayReloadMyServerConfigIni;
      watcher.Created += DelayReloadMyServerConfigIni;
      watcher.Renamed += DelayReloadMyServerConfigIni;
      watcher.EnableRaisingEvents = true;
      return watcher;
    }

    #endregion

    #region Load/ReloadMyServerConfigIni()

    private bool LoadMyServerConfigIni()
    { 
      this.launcher = new Launcher();
      if (!this.launcher.ReadServerConfig())
        return false;

      this.workshop = new Workshop(this.launcher);
      this.launcher.FindRunningServers();
      return true;
    }

    private void DelayReloadMyServerConfigIni(object sender, FileSystemEventArgs e)
    {
      if (e.Name != Path.GetFileName(this.launcher.MainIni.FileName))
        return;
      this.reloadTimer.Stop();
      this.reloadTimer.Start();
    }

    private void ReloadMyServerConfigIni(object sender, EventArgs e)
    {
      Console.WriteLine("\n\nINFO: Reloading modified MyServerConfig.ini");
      LoadMyServerConfigIni();
      ListConfigurations();
      ShowInteractivePrompt();
    }

    #endregion

    #region ListConfigurations()
    private void ListConfigurations()
    {
      Console.WriteLine();
      Console.WriteLine("Available server configurations:");

      var configs = launcher.GetConfigurations();
      foreach (var config in configs)
      {
        string ind = config.IsRunning ? "*" : " ";
        Utils.Write($"^B{config.ID,3}^7: ^A{ind}^7 {config.Name}\n");
      }
    }
    #endregion

    #region ShowInteractivePrompt()
    private void ShowInteractivePrompt()
    {
      Utils.Write("\n^FCommand^7(s) [^Fhelp^7, ^Flist^7, ^Fquit^7, ...] or ^Bserver ID^7(s): ");
    }
    #endregion

    #region ProcessCommand()
    private void ProcessCommand(string cmdOrId)
    {
      int id;

      if (int.TryParse(cmdOrId, out id))
      {
        if (launcher.MainIni.GetSection(Launcher.ServerSectionPrefix + cmdOrId) == null)
        {
          Utils.WriteLine($"^CERROR:^7 No configuration with ID {cmdOrId}");
          return;
        }
        if (action == ServerAction.Start)
        {
          workshop.UpdateWorkshop(false);
          launcher.StartServer(cmdOrId);
        }
        else if (action == ServerAction.Restart)
          launcher.RestartServer(cmdOrId, workshop);
        else if (action == ServerAction.Stop)
          launcher.StopServer(cmdOrId);
        else if (action == ServerAction.Focus)
          launcher.FocusServerConsole(cmdOrId);
      }
      else if (cmdOrId == "-h" || cmdOrId == "-?" || cmdOrId == "help")
        ShowHelp();
      else if (cmdOrId == "-l" || cmdOrId == "list")
        ListConfigurations();
      else if (cmdOrId == "-s" || cmdOrId == "start")
        action = ServerAction.Start;
      else if (cmdOrId == "-r" || cmdOrId == "restart")
        action = ServerAction.Restart;
      else if (cmdOrId == "-x" || cmdOrId == "stop")
        action = ServerAction.Stop;
      else if (cmdOrId == "-f" || cmdOrId == "focus")
        action = ServerAction.Focus;
      else if (cmdOrId == "-uw" || cmdOrId == "updateworkshop")
        workshop.UpdateWorkshop(true);
      else if (cmdOrId == "quit" || cmdOrId == "exit")
        interactive = false;
      else
        ProcessSwitch(cmdOrId);
    }
    #endregion

    #region ProcessSwitch()
    private void ProcessSwitch(string key)
    {
      switch (key.ToLower())
      {
        case "listen":
          launcher.Dedicated = false;
          break;
        case "nosteamsockets":
          launcher.Steamsockets = false;
          break;
        case "noseekfreeloading":
          launcher.Seekfreeloading = false;
          break;
        case "showcommand":
          launcher.ShowCommandLine = true;
          break;
        case "updatetoxikk":
        case "-ut":
          launcher.UpdateToxikk = true;
          break;
        case "cleanworkshop":
        case "-cw":
          launcher.CleanWorkshop = true;
          break;
        case "updateworkshop":
        case "-uw":
          launcher.UpdateWorkshop = true;
          launcher.SyncWorkshop = true;
          break;
        case "syncworkshop":
        case "-sw":
          launcher.SyncWorkshop = true;
          break;
        case "verbose":
        case "-v":
          launcher.Verbose = true;
          break;
        case "interactive":
        case "-i":
          this.interactive = true;
          break;
        default:
          Utils.Write($"^Eunknown command^7: {key}\n");
          break;
      }
    }

    #endregion
  }
}
