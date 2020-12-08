using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Timers;

namespace ToxikkServerLauncher
{
  class CLI
  {
    private const string Version = "2.41";
    private const double WorkshopRedeployMinutes = 1.0;

    [Flags]
    private enum ServerAction { Start = 0x01, Stop = 0x02, Restart = 0x03, Focus = 0x04, Generate = 0x08, Test = 0x10 }

    private Launcher launcher;
    private Workshop workshop;
    private bool interactive;
    private bool updateWorkshop;
    private ServerAction action;
    private DateTime lastWorkshopDeployment = DateTime.MinValue;
    private readonly Timer reloadTimer = new Timer(1000);
    private int exitCode = 0;

    #region Run()
    public int Run(string[] args)
    {
      FileSystemWatcher watcher = null;
      bool showList = false;

      Utils.Write("^AToxikkServerLauncher " + Version + "^7\nhttps://github.com/ToxikkModdingTeam/ToxikkServerLauncher\n");

      if (!LoadMyServerConfigIni())
        return 255;

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
      return exitCode;
    }
    #endregion

    #region AddAutoExecCommandsFromConfig()
    private void AddAutoExecCommandsFromConfig(List<string> commands)
    {
      // auto-run some commands that are set in the config file
      var section = launcher.MainIni.GetSection("ServerLauncher");
      if (section.GetBool("UpdateToxikk"))
        commands.Add("-ut");
      if (section.GetBool("CleanWorkshop"))
        commands.Add("-cw");
      this.updateWorkshop = section.GetBool("UpdateWorkshop");
      if (updateWorkshop)
        commands.Add( "-uw");
      if (section.GetBool("SyncWorkshop", updateWorkshop))
        commands.Add("-sw");
      if (section.GetBool("ShowCommand"))
        commands.Add("showCommand=1");
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
  generate, g:         Generates the specified server id's config folder without starting anything
  test, t:             Test if the specified server(s) are running. Exit code is the number of server that are NOT running
  quit, exit:          Quit the server launcher

^FAdvanced commands^7:
  updateToxikk, ut:    Update TOXIKK game files with steamcmd
  cleanWorkshop, cw:   Delete content of the steamcmd workshop\324810 folder
  updateWorkshop, uw:  Update steam + zip workshop items (implies syncWorkshop)
  us:                  Update steam workshop only (implies syncWorkshop)
  uz:                  Update zip workshop items only (implies syncWorkshop)
  syncWorkshop, sw:    Deploy workshop items to TOXIKK\UDKGame\Workshop and HTTP redirect folder
  showCommand[=1]:     Print the generated TOXIKK.exe command line on screen before starting TOXIKK
  verbose, v[=1]:      More log output
  interactive, i[=1]:  Run in interactive command line interface mode
  steamSockets[=1]:    Append ?steamsockets to the launch URL (can aid NAT traversal, but hanging client connections)
  lan[=1]:             Start server(s) in LAN or internet mode

^FVariables^7:
  @variable@=value     Sets a value for a variable. Inside the INI you can define a default with @variable@ ?= value

^FExperimental commands^7:
  dedicated=0:         Start a listen server instead of a dedicated server
  seekFreeLoading=0:   Don't append -seekfreeloading to the command line

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
      var regexVar = new Regex(@"^(@[0-9A-Za-z_]+@)\s*(.?=)\s*(.*)$");
      Match match;

      if (cmdOrId.StartsWith("-"))
        cmdOrId = cmdOrId.Substring(1);

      if (int.TryParse(cmdOrId, out var id))
        ExecuteAction(cmdOrId);
      else if (cmdOrId == "*")
        GlobAction();
      else if ((match = regexVar.Match(cmdOrId)).Success)
        launcher.SetGlobalVariable(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
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
      else if (cmdOrId == "t" || cmdOrId == "test")
      {
        action = ServerAction.Test;
        exitCode = 0;
      }
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

    #region ExecuteAction()
    private void ExecuteAction(string cmdOrId)
    {
      int id = int.Parse(cmdOrId);
      var secName = id == 0 ? "Client" : Launcher.ServerSectionPrefix + cmdOrId;
      if (launcher.MainIni.GetSection(secName) == null)
      {
        Utils.WriteLine($"^CERROR:^7 No configuration with ID {cmdOrId}");
        return;
      }
      if (action == ServerAction.Start)
      {
        if (this.updateWorkshop)
        {
          bool redeploy = workshop.UpdateWorkshop(false, true, true);
          if (redeploy || (DateTime.Now - lastWorkshopDeployment).TotalMinutes >= WorkshopRedeployMinutes)
          {
            workshop.DeployWorkshopItems();
            lastWorkshopDeployment = DateTime.Now;
          }
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
      else if (action == ServerAction.Test)
      {
        var isRunning = launcher.GetConfigurations().Any(i => i.ID == cmdOrId && i.IsRunning);
        Utils.WriteLine("DedicatedServer" + cmdOrId + " is " + (isRunning ? "" : "^CNOT^7 ") + "running");
        exitCode += isRunning ? 0 : 1;
      }
    }

    #endregion

    #region GlobAction()
    private void GlobAction()
    {
      foreach (var sec in launcher.MainIni.Sections)
      {
        if (!sec.Name.StartsWith(Launcher.ServerSectionPrefix))
          continue;
        string sid = sec.Name.Substring(Launcher.ServerSectionPrefix.Length);
        if (action == ServerAction.Start || action == ServerAction.Generate || action == ServerAction.Test)
          ExecuteAction(sid);
        else if (action == ServerAction.Stop || action == ServerAction.Restart)
        {
          var isRunning = launcher.GetConfigurations().Any(i => i.ID == sid && i.IsRunning);
          if (isRunning)
            ExecuteAction(sid);
        }
      }
    }

    #endregion

    #region ProcessSwitch()
    private void ProcessSwitch(string key)
    {
      var parts = key.Split('=');
      key = parts[0];
      bool on = parts.Length < 2 || ",on,y,yes,t,true,1,".IndexOf("," + parts[1].ToLower() + ",") >= 0;

      switch (key.ToLower())
      {
        case "dedicated":
          launcher.Dedicated = on;
          break;
        case "steamsockets":
          launcher.Steamsockets = on;
          break;
        case "seekfreeloading":
          launcher.Seekfreeloading = on;
          break;
        case "showcommand":
          launcher.ShowCommandLine = on;
          break;
        case "verbose":
        case "v":
          launcher.Verbose = on;
          break;
        case "interactive":
        case "i":
          this.interactive = on;
          break;
        case "lan":
          launcher.Lan = on;
          break;
        default:
          Utils.Write($"^Eunknown command^7: {key}\n");
          break;
      }
    }

    #endregion
  }
}
