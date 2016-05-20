using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ToxikkServerLauncher
{
  class Launcher
  {
    private const string Version = "2.14";
    private const string ServerSectionPrefix = "DedicatedServer";
    private const string ClientSection = "Client";
    private string steamcmdExe;
    private string launcherFolder;
    private string toxikkFolder;
    private string configFolder;
    private string workshopFolder;
    private string httpFolder;
    private string toxikkExe;
    private IniFile mainIni;
    private bool dedicated = true;
    private bool steamsockets = true;
    private bool seekfreeloading = true;
    private bool showCommandLine;
    private bool pause;
    private bool updateToxikk; // update TOXIKK through steamcmd
    private bool cleanWorkshop; // purge steamcmd workshop
    private bool updateWorkshop; // update steamcmd workshop items
    private bool syncWorkshop; // copy steamcmd workshop folder to TOXIKK\Workshop
    private readonly string machineName = Environment.MachineName;

    private static readonly Regex portRegex = new Regex(@"^@port,\s*(\d+),\s*(-?\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex skillClassRegex = new Regex(@"^@skillclass,\s*(\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex serverNumRegx = new Regex(@".*?\\" + ServerSectionPrefix + @"(\d+)");
    private static readonly Regex varNameRegex = new Regex(@"@((?:[A-Za-z_][A-Za-z0-9_]+)|(?:\d+(?:\.\d+)?))@");

    /// <summary>
    /// Maps logical server setting names to real setting names (either ini-file\section\section specifier or a command line option name) as found in the [SimpleNames] section
    /// </summary>
    private readonly Dictionary<string,string> keyMapping = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
    private readonly Dictionary<string,string> globalVariables = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

    #region Run()
    public void Run(string[] args)
    {
      Console.WriteLine("ToxikkServerLauncher " + Version + "\nhttps://github.com/ToxikkModdingTeam/ToxikkServerLauncher");

      launcherFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

      var serverIds = ParseCommandLine(args);
      ReadServerConfig();
      if (!InitFolders())
        return;

      UpdateWorkshop();

      // prompt for server IDs when none were specified on the command line
      if (serverIds.Count == 0)
      {
        Console.WriteLine();
        ListConfigurations();
        Console.Write("Start server(s): ");
        serverIds = (Console.ReadLine() ?? "").Split(' ').ToList();
      }

      if (serverIds[0] == "-h")
        ShowHelp();
      else
      {
        // generate server config(s) and start the server(s)
        foreach (var id in serverIds)
          RunServerConfiguration(id);
      }

      if (this.pause)
        Console.ReadLine();
    }
    #endregion

    #region ParseCommandLine()
    private List<string> ParseCommandLine(string[] args)
    {
      List<string> serverIds = new List<string>();

      foreach (var arg in args)
      {
        if (arg[0] == '/' || arg[0] == '-')
        {
          int idx = arg.IndexOf('=');
          string key = idx < 0 ? arg.Substring(1) : arg.Substring(1, idx-1);
          string val = idx < 0 ? null : arg.Substring(idx + 1);
          switch (key.ToLower())
          {
            case "help":
            case "h":
            case "?":
              this.ShowHelp();
              break;
            case "toxikkdir":
              this.toxikkFolder = val;
              break;
            case "workshopdir":
              this.workshopFolder = val;
              break;
            case "listen":
            case "l":
              this.dedicated = false;
              break;
            case "nosteamsockets":
            case "nss":
              this.steamsockets = false;
              break;
            case "noseekfreeloading":
            case "nsfl":
              this.seekfreeloading = false;
              break;
            case "showcommand":
            case "sc":
              this.showCommandLine = true;
              break;
            case "pause":
            case "p":
              this.pause = true;
              break;
            case "updatetoxikk":
            case "ut":
              this.updateToxikk = true;
              break;
            case "cleanworkshop":
            case "cw":
              this.cleanWorkshop = true;
              break;
            case "updateworkshop":
            case "uw":
              this.updateWorkshop = true;
              this.syncWorkshop = true;
              break;
            case "syncworkshop":
            case "sw":
              this.syncWorkshop = true;
              break;
          }
        }
        else
          serverIds.Add(arg);
      }

      return serverIds;
    }

    #endregion

    #region ShowHelp()
    private void ShowHelp()
    {
      Console.WriteLine(@"
ToxikkServerLauncher [option...] [server number...]
Options (can start with '-' or '/'):
  -help, -h, -?:        This help screen
  -updateToxikk, -ut:   Update TOXIKK with steamcmd
  -cleanWorkshop, -cw:  Delete content of the steamcmd workshop folder
  -updateWorkshop, -uw: Update workshop items with steamcmd (implies -syncWorkshop)
  -syncWorkshop, -sw:   Copy steamcmd workshop folders to TOXIKK\Workshop
  -listen, -l:          Start a listen server instead of a dedicated server
  -noSteamSockets, -ns: Don't append ?steamsockets to the launch URL
  -noSeekFreeLoading, -nsfl: Don't append -seekfreeloading to the command line
  -workshopdir=...      Override the directory from where the launcher will copy workshop content to the TOXIKK folder
  -toxikkdir=...        Override the directory where the launcher will copy files to
  -showcommand, -sc:    Print the generated TOXIKK.exe command line on screen before starting TOXIKK
  -pause, -p:           Wait for Enter key to exit the launcher (used for debugging to prevent closing the window)

More documentation can be found on https://github.com/PredatH0r/ToxikkServerLauncher
");
    }
    #endregion

    #region ReadServerConfig()
    private void ReadServerConfig()
    {
      // rename ServerConfig.ini template to MyServerConfig.ini to prevent overwriting the user config with an update of the launcher
      var myConfigFile = Path.Combine(launcherFolder, "MyServerConfig.ini");
      var configFile = Path.Combine(launcherFolder, "ServerConfig.ini");
      if (!File.Exists(myConfigFile))
        File.Move(configFile, myConfigFile);
      configFile = myConfigFile;
      mainIni = new IniFile(configFile);

      // import old TOXIKK server config file format if necessary
      if (!mainIni.Sections.Any(s => s.Name.StartsWith(ServerSectionPrefix)))
      {
        Console.WriteLine("No [" + ServerSectionPrefix + "...] sections found in ServerConfig.ini. Importing settings from ServerConfigList.ini ...");
        ConvertLegacyServerConfigListIni();
        mainIni = new IniFile(configFile);
      }

      // build dictionary for "simple name" translation
      var section = mainIni.GetSection("SimpleNames");
      if (section != null)
      {
        foreach (var key in section.Keys)
          keyMapping[key] = section.GetString(key) ?? "";
      }

      // process launcher config (first found value wins)
      ReadLauncherConfig(mainIni.GetSection("ServerLauncher:" + machineName));
      ReadLauncherConfig(mainIni.GetSection("ServerLauncher"));
    }
    #endregion

    #region ReadLauncherConfig()
    private void ReadLauncherConfig(IniFile.Section section)
    {
      if (section == null)
        return;

      this.updateToxikk |= section.GetBool("UpdateToxikk");
      this.cleanWorkshop |= section.GetBool("CleanWorkshop");
      this.updateWorkshop |= section.GetBool("UpdateWorkshop");
      this.syncWorkshop |= section.GetBool("SyncWorkshop");

      var steamcmdDir = section.GetString("SteamcmdDir");
      if (steamcmdDir != null && this.steamcmdExe == null)
      {
        var exe = Path.Combine(steamcmdDir, "steamcmd.exe");
        if (File.Exists(exe))
          this.steamcmdExe = exe;
      }

      var workshopDir = section.GetString("WorkshopDir");
      if (workshopDir != null && this.workshopFolder == null && Directory.Exists(workshopDir))
        this.workshopFolder = workshopDir;

      var toxikkDir = section.GetString("ToxikkDir");
      if (!string.IsNullOrEmpty(toxikkDir) && this.toxikkFolder == null)
      {
        var path = Path.Combine(toxikkDir, @"Binaries\win32\TOXIKK.exe");
        if (File.Exists(path))
          this.toxikkFolder = toxikkDir;
        else
          Console.Error.WriteLine("WARNING: ignoring bad ToxikkDir in MyServerConfig.ini");
      }

      if (this.httpFolder == null)
        this.httpFolder = section.GetString("HttpRedirectDir");


      // parse @varname@=value lines
      foreach (var key in section.Keys)
      {
        if (key.Length >= 3 && key.StartsWith("@") && key.EndsWith("@"))
          ProcessVariableDefinition(this.globalVariables, key, ProcessValueMacros("", section.GetString(key), this.globalVariables));
      }
    }
    #endregion

    #region ConvertLegacyServerConfigListIni()
    private void ConvertLegacyServerConfigListIni()
    {
      var oldConfigFile = Path.Combine(toxikkFolder, @"TOXIKKServers\TOXIKKServerLauncher\ServerConfigList.ini");
      if (!File.Exists(oldConfigFile))
        return;

      bool inList = false;
      int serverNumber = 0;

      foreach (var rawLine in File.ReadAllLines(oldConfigFile))
      {
        var line = rawLine.Trim();

        if (line == "" || line.StartsWith(";"))
          continue;

        if (!inList)
        {
          if (line == "BeginList")
            inList = true;
          continue;
        }

        // extract the 14 columns
        var fields = line.Split(' ', '\t').Where(f => f != "").Select(f => f.Trim('"')).ToList();
        for (int i = 0; i < 14; i++)
          fields.Add("");

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.Append("[").Append(ServerSectionPrefix).Append(++serverNumber).AppendLine("]");
        sb.AppendLine($"Map={fields[0]}");
        sb.AppendLine($"GameMode={TranslateGameMode(fields[1])}");
        sb.AppendLine($"GoalScore={fields[2]}");
        sb.AppendLine($"TimeLimit={fields[3]}");
        sb.AppendLine($"MinPlayers={fields[4]}");
        sb.AppendLine($"MaxPlayers={fields[5]}");
        sb.AppendLine($"GamePassword={fields[6]}");
        sb.AppendLine($"AdminPassword={fields[7]}");
        sb.AppendLine($"ServerName={fields[8].Replace('_', ' ')}");
        sb.AppendLine($"GamePort={fields[9]}");
        sb.AppendLine($"QueryPort={fields[10]}");
        sb.AppendLine($"MinSkillClass={fields[11]}");
        sb.AppendLine($"MaxSkillClass={fields[12]}");
        sb.AppendLine($"Mutators={fields[13]}");

        File.AppendAllText(Path.Combine(launcherFolder, "MyServerConfig.ini"), sb.ToString());
      }
    }

    private string TranslateGameMode(string mode)
    {
      if (mode == "BL") return "Cruzade.CRZBloodlust";
      if (mode == "SA") return "Cruzade.CRZTeamGame";
      if (mode == "CC") return "Cruzade.CRZCellCapture";
      if (mode == "AD") return "Cruzade.CRZAreaDomination";
      return mode;
    }
    #endregion

    #region InitFolders()
    private bool InitFolders()
    {
      // ReSharper disable PossibleNullReferenceException
      // ReSharper disable AssignNullToNotNullAttribute

      if (this.toxikkFolder == null)
      {
        if (File.Exists(Path.Combine(this.launcherFolder, @"..\Binaries\Win32\TOXIKK.exe")))
          this.toxikkFolder = Path.Combine(this.launcherFolder, "..");
        else if (this.steamcmdExe != null)
          this.toxikkFolder = Path.Combine(Path.GetDirectoryName(this.steamcmdExe), @"steamapps\common\TOXIKK");
      }
      toxikkFolder = toxikkFolder?.TrimEnd('\\', '/');
      toxikkExe = toxikkFolder == null ? null : Path.Combine(toxikkFolder, @"Binaries\win32\TOXIKK.exe");
      if (toxikkExe == null || !File.Exists(toxikkExe))
      {
        Console.Error.WriteLine("Couldn't find TOXIKK.exe. Please configure ToxikkDir in MyServerConfig.ini or copy+run the launcher from TOXIKK\\TOXIKKServers.");
        return false;
      }

      configFolder = Path.Combine(toxikkFolder, @"UDKGame\Config");

      if (this.workshopFolder == null)
      {
        if (this.steamcmdExe != null)
          this.workshopFolder = Path.Combine(Path.GetDirectoryName(this.steamcmdExe), @"steamapps\workshop\content\324810");
        else if (this.toxikkFolder != null)
          this.workshopFolder = Path.Combine(this.toxikkFolder, @"..\..\workshop\content\324810");
      }

      // ReSharper restore PossibleNullReferenceException
      // ReSharper restore AssignNullToNotNullAttribute

      if (!string.IsNullOrEmpty(httpFolder) && !Directory.Exists(httpFolder))
        Directory.CreateDirectory(httpFolder);

      // set some global variables which can be used inside @CopyFile statements
      this.globalVariables.Add("@ToxikkDir@", this.toxikkFolder);
      this.globalVariables.Add("@WorkshopDir@", this.workshopFolder);
      this.globalVariables.Add("@HttpRedirectDir@", this.httpFolder);

      return true;
    }
    #endregion

    #region UpdateWorkshop()
    private void UpdateWorkshop()
    {
      if (this.cleanWorkshop)
      {
        // delete the manifest file to make sure we really download
        Console.WriteLine("Cleaning " + this.workshopFolder);
        File.Delete(Path.Combine(this.workshopFolder, @"..\..\appworkshop_324810.acf"));

        // delete all numeric folders (which can be reacquired from steam) but keep alphanumeric folders (with developer content)
        foreach (var itemFolder in Directory.GetDirectories(this.workshopFolder))
        {
          long dummy;
          if (long.TryParse(Path.GetFileName(itemFolder), out dummy))
          {
            try { Directory.Delete(itemFolder, true); }
            catch (IOException ex) { Console.Error.WriteLine("ERROR: couldn't delete " + itemFolder + ": " + ex.Message); }
          }
          else
          {
            Console.WriteLine("INFO: keeping non-steam folder " + itemFolder);
          }
        }       
      }

      var workshopItemStatus = new Dictionary<string, bool>();
      var downloadRequired = CheckWorkshopItemStatus(workshopItemStatus);
      if (this.updateToxikk || this.updateWorkshop || downloadRequired)
      {
        if (Process.GetProcessesByName("toxikk").Length >= 1)
          Console.Error.WriteLine("WARNING: TOXIKK.exe is already running, updates may fail.");
        DownloadWorkshopItems(workshopItemStatus);
      }

      if (this.syncWorkshop)
      {
        Console.WriteLine("Copying workshop item contents to TOXIKK and HTTP redirect folders...");
        CopyWorkshopContent();
      }
    }
    #endregion

    #region CheckWorkshopItemStatus()
    private bool CheckWorkshopItemStatus(Dictionary<string, bool> itemStatus)
    {
      bool requireDownload = false;
      var sec = mainIni.GetSection("SteamWorkshop:" + machineName) ?? mainIni.GetSection("SteamWorkshop");
      var items = sec.GetAll("Item");
      foreach (var item in items)
      {
        long id;
        int idx = item.Value.IndexOf(";");
        string nameOrId = idx < 0 ? item.Value : item.Value.Substring(0, idx);
        long.TryParse(nameOrId, out id);
        var dir = Path.Combine(this.workshopFolder, nameOrId);
        var dirExists = Directory.Exists(dir) && Directory.GetDirectories(dir).Length > 0;
        var mustDownload = id != 0 && !dirExists;

        //if (id == 0 && !dirExists)
        //  Console.Error.WriteLine("WARNING: Missing workshop item: " + nameOrId);

        itemStatus[nameOrId] = mustDownload;
        requireDownload |= mustDownload;
      }

      return requireDownload;
    }
    #endregion

    #region DownloadWorkshopItems()
    private void DownloadWorkshopItems(Dictionary<string, bool> items)
    {
      if (items.Count == 0)
        return;

      if (this.steamcmdExe == null)
      {
        Console.Error.WriteLine("WARNING: Steamcmd not configured, skipping workshop updates.");
        return;
      }

      var sec = mainIni.GetSection("SteamWorkshop:" + machineName) ?? mainIni.GetSection("SteamWorkshop");
      if (sec == null)
        return;

      var user = sec.GetString("User");
      var pass = sec.GetString("Password");
      if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
      {
        Console.Error.WriteLine("WARNING: User/Password not configured in [SteamWorkshop], skipping workshop updates.");
        return;
      }

      var sb = new StringBuilder();
      sb.Append("+login ").Append(user).Append(" ").Append(pass);
      if (this.updateToxikk)
        sb.Append(" +force_install_dir \"").Append(this.toxikkFolder).Append("\" +app_update 324810");
      foreach (var item in items)
      {
        int id;
        if ((this.updateWorkshop || item.Value) && int.TryParse(item.Key, out id))
          sb.Append(" +workshop_download_item 324810 ").Append(item.Key);
      }
      sb.Append(" +quit");

      Console.WriteLine("Updating TOXIKK and Steam Workshop Items...\n");
      var psi = new ProcessStartInfo(this.steamcmdExe, sb.ToString());
      psi.UseShellExecute = false;
      var proc = Process.Start(psi);
      proc?.WaitForExit();
      Console.WriteLine("\nSteam update complete.\n");
    }
    #endregion

    #region CopyWorkshopContent()
    private void CopyWorkshopContent()
    {
      if (!Directory.Exists(workshopFolder))
        return;

      if (this.httpFolder == null)
        Console.Error.WriteLine("WARNING: no HTTP redirect folder configured. Clients won't be able to auto-download workshop items.");

      var toxikkWorkshopDir = Path.Combine(this.toxikkFolder, @"UDKGame\Workshop");
      try
      {
        // delete existing files so only content of the workshop items listed in the .ini survives
        if (Directory.Exists(toxikkWorkshopDir))
          Directory.Delete(toxikkWorkshopDir, true);
      }
      catch (IOException ex)
      {
        Console.Error.WriteLine("Failed to delete " + toxikkWorkshopDir + ": " + ex.Message);
      }

      foreach (var itemSetting in this.mainIni.GetSection("SteamWorkshop").GetAll("Item"))
      {
        // remove trailing comment
        var item = itemSetting.Value;
        int idx = item.IndexOf(";");
        if (idx >= 0)
          item = item.Substring(0, idx).Trim();

        var itemPath = Path.Combine(this.workshopFolder, item);
        try
        {
          if (Directory.Exists(itemPath))
            CopyFolder(itemPath, toxikkWorkshopDir);
          else
            Console.Error.WriteLine("WARNING: Workshop item folder not found: " + itemPath);
        }
        catch (IOException ex)
        {
          Console.Error.WriteLine("Failed to copy workshop item " + itemPath + ": " + ex.Message);
        }
      }
    }
    #endregion

    #region CopyFolder()
    private void CopyFolder(string sourceDir, string targetDir)
    {
      // ReSharper disable AssignNullToNotNullAttribute
      foreach (var file in Directory.GetFiles(sourceDir))
      {
        // copy files to toxikk/udkgame/workshop/....
        var target = Path.Combine(targetDir, Path.GetFileName(file));
        if (File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(target) || new FileInfo(file).Length != new FileInfo(target).Length)
          FileCopy(file, target, true);

        // copy files to HTTP redirect
        if (!string.IsNullOrEmpty(httpFolder) && ".udk.upk.u".Contains(Path.GetExtension(file)))
        {
          target = Path.Combine(httpFolder, Path.GetFileName(file));
          if (File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(target) || new FileInfo(file).Length != new FileInfo(target).Length)
            FileCopy(file, target, true);
        }
      }

      // recursively process subdirectories
      foreach (var dir in Directory.GetDirectories(sourceDir))
        CopyFolder(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
      // ReSharper restore AssignNullToNotNullAttribute
    }
    #endregion

    #region ListConfigurations()
    private void ListConfigurations()
    {
      Console.WriteLine("TOXIKK Server Launcher (use -h for help)");
      Console.WriteLine("Available server configurations:");
      if (mainIni.GetSection(ClientSection) != null)
        Console.WriteLine("  0: update base configuration and start client");
      foreach (var section in mainIni.Sections)
      {
        if (section.Name.StartsWith(ServerSectionPrefix) && !section.Name.Contains(":"))
        {
          var name = section.GetString("@ServerName") ?? section.GetString("ServerName");
          name = ProcessValueMacros("", name, this.globalVariables);
          Console.WriteLine($"{section.Name.Substring(ServerSectionPrefix.Length),3}: {name}");
        }
      }
    }
    #endregion

    #region RunServerConfiguration()
    private void RunServerConfiguration(string serverId)
    {
      if (serverId.Trim() == "")
        return;

      var sectionName = serverId == "0" ? ClientSection : ServerSectionPrefix + serverId;
      var section = mainIni.GetSection(sectionName);
      if (section == null)
      {
        Console.Error.WriteLine("No configuration section for " + sectionName);
        return;
      }

      if (serverId == "0")
        this.dedicated = false;

      var name = section.GetString("@ServerName") ?? ProcessValueMacros("", section.GetString("ServerName"), globalVariables) ?? sectionName;
      Console.WriteLine("\nStarting " + name);
      string map, options, cmdArgs;
      if (GenerateConfig(mainIni, section, out map, out options, out cmdArgs))
      {
        if (serverId == "0")
          Process.Start("steam://rungameid/324810");
        else
          LaunchServer(map, options, cmdArgs, sectionName);
      }
    }

    #endregion

    #region GenerateConfig()
    private bool GenerateConfig(IniFile iniFile, IniFile.Section section, out string map, out string options, out string cmdArgs)
    {
      var targetConfigFolder = this.dedicated ? Path.Combine(configFolder, section.Name) : this.configFolder.TrimEnd('\\', '/');
      CopyIniFilesToServerConfigFolder(targetConfigFolder, section);

      var destIniCache = new Dictionary<string, IniFile>();
      var optionDict = new SortedDictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);
      var variableDict = new Dictionary<string, string>(globalVariables, StringComparer.InvariantCultureIgnoreCase);
      variableDict["@ConfigDir@"] = targetConfigFolder;


      // default command line args, can be modified with @cmdline =, +=, -=
      cmdArgs = dedicated ? "-configsubdir=" + section.Name + " -nohomedir -unattended" : "-log -nostartupmovies";
      map = null;

      // recursive processing of a section and its @Import sections, then override any section with a machine-specific section
      ProcessConfigSection(targetConfigFolder, "", iniFile, section, destIniCache, optionDict, variableDict, ref cmdArgs);
      ProcessConfigSection(targetConfigFolder, "", iniFile, iniFile.GetSection(section.Name + ":" + machineName), destIniCache, optionDict, variableDict, ref cmdArgs);

      // build URL with map name and options
      if (!section.Name.StartsWith(ClientSection) && !optionDict.TryGetValue("map", out map))
      {
        Console.Error.WriteLine("ERROR: No map specified");
        options = null;
        return false;
      }

      optionDict.Remove("map");
      var sbOptions = new StringBuilder();
      foreach (var entry in optionDict)
        sbOptions.Append("?").Append(entry.Key).Append("=").Append(entry.Value.Replace(' ', '_'));

      // save the ini files
      foreach (var destIni in destIniCache.Values)
        destIni.Save();

      options = sbOptions.ToString();
      return true;
    }
    #endregion

    #region CopyIniFilesToServerConfigFolder()
    private void CopyIniFilesToServerConfigFolder(string targetConfigFolder, IniFile.Section section)
    {
      if (this.dedicated)
      {
        // delete all files from the target config folder, except those matching a @Keep=... pattern
        if (Directory.Exists(targetConfigFolder))
          ClearDirectory(targetConfigFolder, GetFilesToKeep(section));

        // copy all Default*.ini files
        foreach (var file in Directory.GetFiles(configFolder, "Default*.ini"))
          FileCopy(file, Path.Combine(targetConfigFolder, Path.GetFileName(file) ?? ""), true);

        // copy UDK*.ini where there is no matching Default*.ini
        // (sometimes UDK* files are accessed before they have been generated from Default* files)
        foreach (var file in Directory.GetFiles(configFolder, "UDK*.ini"))
        {
          var fileName = Path.GetFileName(file) ?? "";
          var defaultFile = Path.Combine(Path.GetDirectoryName(file) ?? "", "Default" + fileName.Substring(3));
          if (!File.Exists(defaultFile))
            FileCopy(file, Path.Combine(targetConfigFolder, fileName), true);
        }
      }

      // copy all UDK*.ini files from the ServerLauncher folder
      foreach (var file in Directory.GetFiles(launcherFolder, "UDK*.ini"))
      {
        var fileName = Path.GetFileName(file) ?? "";
        FileCopy(file, Path.Combine(targetConfigFolder, fileName), true);
      }

      if (this.dedicated)
      {
        // copy all *.ini files from Workshop/Config folder (but don't overwrite existing files)
        var dir = Path.Combine(this.toxikkFolder, @"UDKGame\Workshop\Config");
        if (Directory.Exists(dir))
        {
          foreach (var file in Directory.GetFiles(dir, "*.ini"))
          {
            var target = Path.Combine(targetConfigFolder, Path.GetFileName(file) ?? "");
            if (!File.Exists(target))
              FileCopy(file, target, false);
          }
        }
      }
    }
    #endregion

    #region GetFilesToKeep, ClearDirectory, KeepExistingFile

    public List<Regex> GetFilesToKeep(IniFile.Section section)
    {
      List<Regex> list = new List<Regex>();

      // convert file name globs like "My*.ini" to regular expressions

      foreach (var keep in section.GetAll("@keep"))
      {
        foreach (var glob in SplitUnquoted(keep.Value, ','))
        {
          var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*?").Replace(@"\?", ".") + "$";
          list.Add(new Regex(pattern));
        }
      }
      return list;
    }

    private bool ClearDirectory(string path, List<Regex> filesToKeep)
    {
      bool empty = true;
      foreach (var dir in Directory.GetDirectories(path))
      {
        if (ClearDirectory(dir, filesToKeep))
          Directory.Delete(dir);
        else
          empty = false;
      }

      foreach (var file in Directory.GetFiles(path))
      {
        if (KeepExistingFile(file, filesToKeep))
          empty = false;
        else
          File.Delete(file);
      }
      return empty;
    }

    private bool KeepExistingFile(string file, List<Regex> filesToKeep)
    {
      file = Path.GetFileName(file) ?? "";
      foreach (var regex in filesToKeep)
      {
        if (regex.IsMatch(file))
          return true;
      }
      return false;
    }
    #endregion

    #region FileCopy()
    /// <summary>
    /// Directory.Delete(dir, true) + Directory.CreateDirectory(dir) has some race condition that may cause the dir to be deleted AFTER it was re-created.
    /// This hacky method retries to create the target dir in case the initial copy fails.
    /// </summary>
    private void FileCopy(string source, string dest, bool overwrite)
    {
      var dir = Path.GetDirectoryName(dest) ?? "";
      if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);
      else if (!overwrite && File.Exists(dest))
        return;

      try
      {
        File.Copy(source, dest, overwrite);
      }
      catch (IOException)
      {
        Directory.CreateDirectory(dir);
        File.Copy(source, dest, overwrite);
      }
    }
    #endregion

    #region ProcessConfigSection()
    private void ProcessConfigSection(string targetConfigFolder, string configSourceFolder, IniFile iniFile, IniFile.Section section, 
      Dictionary<string, IniFile> destIniCache, SortedDictionary<string,string> options, Dictionary<string, string> variables, ref string cmdArgs)
    {
      if (section == null)
        return;
      foreach (var unmappedKey in section.Keys)
      {
        foreach (var rawValue in section.GetAll(unmappedKey))
        {
          var loopInfo = ProcessCrossProductLoop(rawValue.Value, targetConfigFolder, variables);
          foreach (var loopArgs in loopInfo.CombinationValues)
          {
            // set variables for the current combination
            for (int i = 0; i < loopArgs.Count; i++)
            {
              variables["@" + (i + 1) + "@"] = loopArgs[i];
              var parts = SplitUnquoted(loopArgs[i], '|');
              for (int j = 0; j < parts.Length; j++)
                variables["@" + (i + 1) + "." + (j + 1) + "@"] = parts[j];
            }

            var operation = rawValue.Operator; // =, += or -=
            var value = ProcessValueMacros(targetConfigFolder, loopInfo.Template, variables);

            if (unmappedKey.StartsWith("@"))
            {
              var lowerKey = unmappedKey.ToLower();
              if (lowerKey == "@import")
                ProcessImport(targetConfigFolder, configSourceFolder, iniFile, destIniCache, options, variables, ref cmdArgs, value);
              else if (lowerKey == "@copy" || lowerKey == "@copyfiles") // @copyfiles is legacy name
                ProcessCopyFile(targetConfigFolder, configSourceFolder, value);
              else if (lowerKey == "@cmdline")
                ProcessCommandLineArg(ref cmdArgs, operation, value);
              else if (lowerKey.Length >= 3 && lowerKey.EndsWith("@"))
                ProcessVariableDefinition(variables, lowerKey, value);
              else if (lowerKey == "@servername" || lowerKey == "@keep")
              {
              }
              else
                Console.Error.WriteLine("WARNING: ignoring unknown directive: " + unmappedKey + "=" + rawValue.Value);
            }
            else
            {
              string mappedKey;
              if (!keyMapping.TryGetValue(unmappedKey, out mappedKey))
                mappedKey = unmappedKey;

              var configMapping = mappedKey.Split('\\');
              if (configMapping.Length == 3)
                ProcessIniSetting(targetConfigFolder, destIniCache, operation, configMapping, value);
              else
                ProcessUrlParameter(options, operation, mappedKey, value);
            }
          }
        }
      }
    }
    #endregion

    #region ProcessCrossProductLoop()
    /// <summary>
    /// Create combinations with the cross product of all lists in a @loop -or- when there is no @loop, return the value as-is
    /// </summary>
    private LoopInfo ProcessCrossProductLoop(string rawValue, string targetConfigFolder, Dictionary<string,string> variables)
    {
      // no @loop, return 1 static entry 
      if (!rawValue.ToLower().StartsWith("@loop "))
        return new LoopInfo(rawValue);

      rawValue = ProcessValueMacros(targetConfigFolder, rawValue, variables, false);

      int colon;
      var loops = ExtractLoopList(rawValue, out colon);
      if (loops.Count == 0)
      {
        Console.Error.WriteLine("WARNING: bad @loop statement: " + rawValue);
        return new LoopInfo();
      }

      var combinationCount = 1;
      List<string[]> loopVals = new List<string[]>(loops.Count);
      foreach (var loop in loops)
      {
        var vals = SplitUnquoted(loop, ',');
        loopVals.Add(vals);
        combinationCount *= vals.Length;
      }
      var result = new List<List<string>>();
      for (int i = 0; i < combinationCount; i++)
      {
        int j = i;
        var combination = new List<string>(loops.Count);
        for (int l = loops.Count - 1; l >= 0; l--)
        {
          combination.Insert(0, loopVals[l][j%loopVals[l].Length]);
          j /= loopVals[l].Length;
        }
        result.Add(combination);
      }
      return new LoopInfo(rawValue.Substring(colon+1), result);
    }

    /// <summary>
    /// Extract the text enclosed by {...} until we find a ':'
    /// </summary>
    private List<string> ExtractLoopList(string rawValue, out int colon)
    {
      List<string> loops = new List<string>();
      colon = -1;
      for (int i = 6, len = rawValue.Length; i < len;)
      {
        int open = rawValue.IndexOf('{', i);
        colon = rawValue.IndexOf(':', i);
        if (open >= 0 && open < colon)
        {
          int close = rawValue.IndexOf('}', open);
          if (close < 0)
          {
            colon = -1;
            break;
          }
          loops.Add(rawValue.Substring(open + 1, close - (open + 1)));
          i = close + 1;
        }
        else if (colon >= 0 || open < 0)
          break;
      }

      if (colon < 0)
        loops.Clear();
      return loops;
    }

    #endregion

    #region ProcessValueMacros()
    private string ProcessValueMacros(string targetConfigFolder, string value, Dictionary<string, string> variables, bool expandLoopVars = true)
    {
      if (value == null)
        return null;

      Match match;

      // replace @var-name@ with the variable value that was set with "@var-name@=..."
      var newVal = value;
      for (match = varNameRegex.Match(value); match.Success; match = match.NextMatch())
      {
        var varName = match.Groups[0].Value;
        string varValue;
        if (!variables.TryGetValue(varName, out varValue))
          varValue = "";

        if (char.IsDigit(varName[1]))
        {
          if (!expandLoopVars)
            continue;
          varValue = varValue.Trim('"');
        }

        newVal = newVal.Replace(varName, varValue);
      }
      value = newVal;

      // @port,base,multiplier macro to auto-generate port numbers
      var port = portRegex.Match(value);
      var serv = serverNumRegx.Match(targetConfigFolder);
      if (port.Success && serv.Success)
        return (int.Parse(port.Groups[1].Value) + int.Parse(port.Groups[2].Value)*(int.Parse(serv.Groups[1].Value) - 1)).ToString();

      // @SkillClass,skillclass-value
      match = skillClassRegex.Match(value);
      if (match.Success)
        return (int.Parse(match.Groups[1].Value)/1.5f).ToString();

      // @Env,environment-variable-name
      if (value.StartsWith("@env,", StringComparison.InvariantCultureIgnoreCase))
        return Environment.GetEnvironmentVariable(value.Substring(4).Trim()) ?? "";

      // @Id
      if (StringComparer.CurrentCultureIgnoreCase.Compare(value, "id") == 0)
        return serv.Success ? serv.Groups[1].Value : "";
        
      return value;
    }

    #endregion

    #region ProcessIniSetting()
    /// <summary>
    /// process a "filename\section\key=value" entry
    /// </summary>
    private static void ProcessIniSetting(string targetConfigFolder, Dictionary<string, IniFile> destIniCache, string operation, string[] configMapping, string value)
    {
      IniFile destIni;
      string iniFile = string.IsNullOrEmpty(Path.GetExtension(configMapping[0])) ? configMapping[0] + ".ini" : configMapping[0];
      string destIniPath = Path.Combine(targetConfigFolder, iniFile);
      if (!destIniCache.TryGetValue(destIniPath, out destIni))
      {
        destIni = new IniFile(destIniPath);
        destIniCache.Add(destIniPath, destIni);
      }
      var destSec = destIni.GetSection(configMapping[1], true);
      if (operation == "=")
        destSec.Set(configMapping[2], value);
      else if (operation == ":=")
        destSec.Remove(configMapping[2]);
      else if (operation == ".=")
        destSec.Add(configMapping[2], value);
      else if (operation == "+=")
      {
        if (destSec.GetAll(configMapping[2]).All(item => item.Value != value))
          destSec.Add(configMapping[2], value);
      }
      else if (operation == "*=")
      {
        if (destSec.GetAll(configMapping[2]).All(item => item.Value != value))
          destSec.Insert(configMapping[2], value, 0);
      }
      else if (operation == "-=")
        destSec.Remove(configMapping[2], value);
    }

    #endregion

    #region ProcessImport()
    /// <summary>
    /// recursively process settings from another section or file
    /// </summary>
    private void ProcessImport(string targetConfigFolder, string configSourceFolder, IniFile iniFile, Dictionary<string, IniFile> destIniCache, 
      SortedDictionary<string, string> options, Dictionary<string, string> variables, ref string cmdArgs, string value)
    {
      var regexImportFromOtherFile = new Regex(@"^(?:(.*)[/\\])?(\S+\.ini)(\\\S+)?$", RegexOptions.IgnoreCase);

      foreach (var import in value.Split(','))
      {
        var match = regexImportFromOtherFile.Match(import);
        if (match.Success)
        {
          var subFolder = match.Groups[1].Success ? Path.Combine(configSourceFolder, match.Groups[1].Value) : configSourceFolder;
          var subFile = match.Groups[2].Value;
          var subSection = match.Groups[3].Success ? match.Groups[3].Value.Substring(1) : Path.GetFileName(targetConfigFolder);
          var subIni = new IniFile(Path.Combine(this.launcherFolder, subFolder, subFile));
          var sec = subIni.GetSection(subSection);
          if (sec == null)
            Console.Error.WriteLine("WARNING: @import={0}: failed to locate {1}{2}\\{3}", value, subFolder, subFile, subSection);
          else
          {
            ProcessConfigSection(targetConfigFolder, subFolder, subIni, sec, destIniCache, options, variables, ref cmdArgs);
            ProcessConfigSection(targetConfigFolder, subFolder, subIni, subIni.GetSection(subSection + ":" + machineName), destIniCache, options, variables, ref cmdArgs);
          }
        }
        else // import section from same file
        {
          var sec = iniFile.GetSection(import);
          if (sec == null)
            Console.Error.WriteLine("WARNING: @import={0}: failed to locate [{1}]", value, import);
          else
          {
            ProcessConfigSection(targetConfigFolder, configSourceFolder, iniFile, sec, destIniCache, options, variables, ref cmdArgs);
            ProcessConfigSection(targetConfigFolder, configSourceFolder, iniFile, iniFile.GetSection(import + ":" + machineName), destIniCache, options, variables, ref cmdArgs);
          }
        }
      }
    }

    #endregion

    #region ProcessCopyFile()
    /// <summary>
    /// copy files specified as "source:dest,source:dest,...". 
    /// </summary>
    private void ProcessCopyFile(string targetConfigFolder, string configSourceFolder, string value)
    {
      // source files can be placed in the ServerLauncher folder or the template config folder
      foreach (var untrimmedFileInfo in value.Split(','))
      {
        var fileInfo = untrimmedFileInfo.Trim();
        int idx = fileInfo.Length > 2 ? fileInfo.IndexOf(':', 2) : -1; // ':' is used as source:target separator, but could also be a drive-letter separator
        var names = idx <= 2 ? new[] { fileInfo } : new[] {fileInfo.Substring(0, idx), fileInfo.Substring(idx + 1)};
        var sourceName = names[0].Trim();
        var destName = names.Length == 2 ? names[1].Trim() : Path.IsPathRooted(sourceName) ? Path.GetFileName(sourceName) : sourceName;
        if (sourceName != "" && destName != "")
        {
          var folder = File.Exists(Path.Combine(launcherFolder, configSourceFolder, sourceName)) ? Path.Combine(launcherFolder, configSourceFolder) : configFolder;
          var source = Path.Combine(folder, sourceName);
          if (File.Exists(source))
            FileCopy(source, Path.Combine(targetConfigFolder, destName), true);
          else
            Console.Error.WriteLine("WARNING: @copy source not found: " + sourceName);
        }
      }
    }
    #endregion

    #region ProcessCommandLineArg()
    private static void ProcessCommandLineArg(ref string cmdArgs, string operation, string value)
    {
      if (operation == "=")
        cmdArgs = value;
      else if (operation == "+=")
      {
        if (!cmdArgs.Contains(" " + value + " ") && !cmdArgs.EndsWith(" " + value))
          cmdArgs += " " + value;
      }
      else if (operation == "*=")
      {
        if (!cmdArgs.Contains(" " + value + " ") && !cmdArgs.EndsWith(" " + value))
          cmdArgs = value + " " + cmdArgs;
      }
      else if (operation == "-=")
        cmdArgs = cmdArgs.Replace(value, "").Replace("  ", " ").Trim();
    }
    #endregion

    #region ProcessVariableDefinition()
    private static void ProcessVariableDefinition(Dictionary<string, string> variables, string mappedKey, string value)
    {
      variables[mappedKey] = value;
    }
    #endregion

    #region ProcessUrlParameter()
    private static void ProcessUrlParameter(SortedDictionary<string, string> options, string operation, string mappedKey, string value)
    {
      if (operation == "=")
        options[mappedKey] = value;
      else if (operation == ":=")
        options.Remove(mappedKey);
      else if (operation == "+=")
      {
        string oldValue;
        if (!options.TryGetValue(mappedKey, out oldValue) || (oldValue != value && !oldValue.Contains("," + value)))
          options[mappedKey] = oldValue == null ? value : oldValue + "," + value;
      }
      else if (operation == "*=")
      {
        string oldValue;
        if (!options.TryGetValue(mappedKey, out oldValue) || (oldValue != value && !oldValue.Contains("," + value)))
          options[mappedKey] = oldValue == null ? value : value + "," + oldValue;
      }
      else if (operation == "-=")
      {
        string oldValue;
        if (options.TryGetValue(mappedKey, out oldValue))
          options[mappedKey] = oldValue.Replace(value, "").Replace(",,", ",").Trim(',');
      }
    }

    #endregion

    #region LaunchServer()
    private void LaunchServer(string map, string options, string cmdArgs, string sectionName)
    {
      string args = "";
      if (dedicated)
        args = "server ";

      args += map;

      if (dedicated)
        args += "?dedicated=true";
      else
        args += "?listen=true";

      args += options;

      if (steamsockets)
        args += "?steamsockets";

      if (this.seekfreeloading)
        cmdArgs += " -seekfreeloading";

      if (cmdArgs.Length > 0)
        args += " " + cmdArgs;

      if (this.showCommandLine)
        Console.WriteLine("INFO: starting " + toxikkExe + " " + args);

      Environment.CurrentDirectory = Path.GetDirectoryName(toxikkExe) ?? "";
      if (Process.Start(toxikkExe, args) == null)
        Console.Error.WriteLine("Couldn't start TOXIKK for " + sectionName);
    }
    #endregion

    #region SplitUnquoted()
    private string[] SplitUnquoted(string input, char separator)
    {
      List<string> parts = new List<string>();
      StringBuilder part = new StringBuilder();
      bool inQuotes = false;
      for (int i = 0, len = input.Length; i < len; i++)
      {
        var ch = input[i];
        if (ch == '"')
          inQuotes = !inQuotes;
        if (ch == separator && !inQuotes)
        {
          parts.Add(part.ToString());
          part.Clear();
        }
        else
          part.Append(ch);
      }
      parts.Add(part.ToString());
      return parts.ToArray();
    }
    #endregion
  }

  #region class LoopInfo
  class LoopInfo
  {
    public readonly string Template;
    public readonly List<List<string>> CombinationValues;

    public LoopInfo()
    {      
    }

    public LoopInfo(string noLoopTemplate)
    {
      Template = noLoopTemplate;
      CombinationValues = new List<List<string>> {new List<string>()};
    }

    public LoopInfo(string loopTemplate, List<List<string>> combinationValues)
    {
      Template = loopTemplate;
      CombinationValues = combinationValues;
    }
  }
  #endregion
}
