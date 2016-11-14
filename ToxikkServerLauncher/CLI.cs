using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

namespace ToxikkServerLauncher
{
  class CLI
  {
    private const string Version = "2.31";
    private const double WorkshopRedeployMinutes = 1.0;

    [Flags]
    private enum ServerAction { Start = 0x01, Stop = 0x02, Restart = 0x03, Focus = 0x04, Generate = 0x08 }

    private Launcher launcher;
    private Workshop workshop;
    private bool interactive;
    private ServerAction action;
    private DateTime lastWorkshopDeployment = DateTime.MinValue;
    private readonly Timer reloadTimer = new Timer(1000);

    #region Run()
    public void Run(string[] args)
    {
      FileSystemWatcher watcher = null;
      bool showList = false;

      Utils.Write("^AToxikkServerLauncher " + Version + "^7\nhttps://github.com/ToxikkModdingTeam/ToxikkServerLauncher\n");

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
        showList = true;
      }

      AddAutoExecCommandsFromConfig(commands);

      do
      {
        action = ServerAction.Start;
        if (interactive)
        {
          if (showList)
          {
            ListConfigurations();
            showList = false;
          }
          ShowInteractivePrompt();
          commands = (Console.ReadLine() ?? "").Split(' ').ToList();
          if (commands.Count == 1 && commands[0] == "")
            continue;
        }

        launcher.FindRunningServers();
        foreach (var cmdOrId in commands)
          ProcessCommand(cmdOrId);
      } while (interactive);

      watcher?.Dispose();
    }
    #endregion

    #region AddAutoExecCommandsFromConfig()
    private void AddAutoExecCommandsFromConfig(List<string> commands)
    {
      // auto-run some commands that are set in the config file
      var section = launcher.MainIni.GetSection("ServerLauncher");
      if (section.GetBool("UpdateWorkshop"))
        commands.Insert(0, "-uw");
      if (section.GetBool("CleanWorkshop"))
        commands.Insert(0, "-cw");
      if (section.GetBool("UpdateToxikk"))
        commands.Insert(0, "-ut");
    }
    #endregion

    #region ShowHelp()
    private void ShowHelp()
    {
      Utils.Write(@"
^AToxikkServerLauncher^7 [^Fcommand^7 | ^Bserver-id^7]...

Multiple commands and server numbers can be mixed, e.g.: stop 1 start 2
The full documentation can be found on https://github.com/PredatH0r/ToxikkServerLauncher

^FBasic commands^7:
  help, h, ?:          This help screen
  list, l:             Lists the available servers and their status
  start, s:            Start servers with the following ids
  restart, r:          Restart servers with following ids
  stop, x:             Stop servers with the following ids
  focus, f:            Focuses the console window of the specified server id
  quit, exit:          Quit the server launcher

^FAdvanced commands^7:
  updateToxikk, ut:    Update TOXIKK game files with steamcmd
  cleanWorkshop, cw:   Delete content of the steamcmd workshop\324810 folder
  updateWorkshop, uw:  Update steam + zip workshop items (implies syncWorkshop)
  us:                  Update steam workshop only (implies syncWorkshop)
  uz:                  Update zip workshop items only (implies syncWorkshop)
  syncWorkshop, sw:    Deploy workshop items to TOXIKK\UDKGame\Workshop and HTTP redirect folder
  showCommand:         Print the generated TOXIKK.exe command line on screen before starting TOXIKK
  verbose, v:          More log output
  interactive, i:      Run in interactive command line interface mode
  noSteamSockets:      Don't append ?steamsockets to the launch URL (can fix hanging client connections)
  lan, inet:           Start server(s) in LAN or internet mode

^FExperimental commands^7:
  listen:              Start a listen server instead of a dedicated server
  noSeekFreeLoading:   Don't append -seekfreeloading to the command line

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

    private volatile bool loading;

    private void DelayReloadMyServerConfigIni(object sender, FileSystemEventArgs e)
    {
      var ini = this.launcher.MainIni?.FileName;
      if (ini == null || e.Name != Path.GetFileName(ini))
        return;
      this.reloadTimer.Stop();
      this.reloadTimer.Start();
    }

    private void ReloadMyServerConfigIni(object sender, EventArgs e)
    {
      if (loading)
        return;
      Console.WriteLine("\n\nINFO: Reloading modified MyServerConfig.ini");
      LoadMyServerConfigIni();
      ListConfigurations();
      ShowInteractivePrompt();
    }

    private bool LoadMyServerConfigIni()
    {
      if (loading)
        return false;

      try
      {
        loading = true;
        this.launcher = new Launcher();
        if (!this.launcher.ReadServerConfig())
          return false;

        this.workshop = new Workshop(this.launcher);
        this.launcher.FindRunningServers();
        return true;
      }
      finally
      {
        loading = false;
      }
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

      if (cmdOrId.StartsWith("-"))
        cmdOrId = cmdOrId.Substring(1);

      if (int.TryParse(cmdOrId, out id))
      {
        var secName = id == 0 ? "Client" : Launcher.ServerSectionPrefix + cmdOrId;
        if (launcher.MainIni.GetSection(secName) == null)
        {
          Utils.WriteLine($"^CERROR:^7 No configuration with ID {cmdOrId}");
          return;
        }
        if (action == ServerAction.Start)
        {
          bool redeploy = workshop.UpdateWorkshop(false, true, true);
          if (redeploy || (DateTime.Now - lastWorkshopDeployment).TotalMinutes >= WorkshopRedeployMinutes)
          {
            workshop.DeployWorkshopItems();
            lastWorkshopDeployment = DateTime.Now;
          }
          launcher.StartServer(cmdOrId);
        }
        else if (action == ServerAction.Generate)
          launcher.GenerateConfig(id);
        else if (action == ServerAction.Restart)
          launcher.RestartServer(cmdOrId, workshop);
        else if (action == ServerAction.Stop)
          launcher.StopServer(cmdOrId);
        else if (action == ServerAction.Focus)
          launcher.FocusServerConsole(cmdOrId);
      }
      else if (cmdOrId == "h" || cmdOrId == "?" || cmdOrId == "help")
        ShowHelp();
      else if (cmdOrId == "l" || cmdOrId == "list")
        ListConfigurations();
      else if (cmdOrId == "g" || cmdOrId == "generate")
        action = ServerAction.Generate;
      else if (cmdOrId == "s" || cmdOrId == "start")
        action = ServerAction.Start;
      else if (cmdOrId == "r" || cmdOrId == "restart")
        action = ServerAction.Restart;
      else if (cmdOrId == "x" || cmdOrId == "stop")
        action = ServerAction.Stop;
      else if (cmdOrId == "f" || cmdOrId == "focus")
        action = ServerAction.Focus;
      else if (cmdOrId == "ut" || cmdOrId == "updatetoxikk")
        workshop.UpdateToxikk(true);
      else if (cmdOrId == "cw" || cmdOrId == "cleanworkshop")
        workshop.CleanWorkshopFolder();
      else if (cmdOrId == "uw" || cmdOrId == "updateworkshop")
      {
        if (workshop.UpdateWorkshop(true, true, true))
          lastWorkshopDeployment = DateTime.MinValue;
      }
      else if (cmdOrId == "us")
      {
        if (workshop.UpdateWorkshop(true, true, false))
          lastWorkshopDeployment = DateTime.MinValue;
      }
      else if (cmdOrId == "uz")
      {
        if (workshop.UpdateWorkshop(true, false, true))
          lastWorkshopDeployment = DateTime.MinValue;
      }
      else if (cmdOrId == "sw" || cmdOrId == "syncworkshop")
      {
        workshop.DeployWorkshopItems();
        lastWorkshopDeployment = DateTime.Now;
      }
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
        case "verbose":
        case "v":
          launcher.Verbose = true;
          break;
        case "interactive":
        case "i":
          this.interactive = true;
          break;
        case "lan":
          launcher.Lan = true;
          break;
        case "inet":
          launcher.Lan = false;
          break;
        default:
          Utils.Write($"^Eunknown command^7: {key}\n");
          break;
      }
    }

    #endregion
  }
}
