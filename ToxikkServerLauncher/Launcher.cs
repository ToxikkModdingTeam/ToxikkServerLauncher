﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ToxikkServerLauncher
{
  public class Launcher
  {
    public const string ServerSectionPrefix = "DedicatedServer";
    public const string ClientSection = "Client";

    private static readonly Regex portRegex = new Regex(@"^@port,\s*(\d+),\s*(-?\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex skillClassRegex = new Regex(@"^@(?:sc|skillclass),\s*(\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex serverNumRegx = new Regex(@".*?[\\/]" + ServerSectionPrefix + @"(\d+)");
    private static readonly Regex varNameRegex = new Regex(@"@((?:[A-Za-z_][A-Za-z0-9_]+)|(?:\d+(?:\.\d+)?))@");
    private static readonly int[] SkillClassDifficulty = {0, 0, 1, 2, 3, 3, 4, 4, 5, 5, 5, 6, 7};

    private readonly string launcherFolder;
    private readonly HashSet<string> runningServers = new HashSet<string>();
    private readonly Dictionary<string, string> simpleNames = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
    private readonly Dictionary<string, string> globalVariables = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

    public string SteamcmdExe { get; private set; }
    public string ToxikkFolder { get; private set; }
    public string ConfigFolder { get; private set; }
    public string WorkshopFolder { get; private set; }
    public string HttpFolder { get; private set; }
    public string ToxikkExe { get; private set; }
    public IniFile MainIni { get; private set; }
    public bool Dedicated { get; set; } = true;
    public bool Server { get; set; } = true;
    public bool Lan { get; set; } = false;
    public bool Steamsockets { get; set; }
    public bool Seekfreeloading { get; set; } = true;
    public bool Verbose { get; set; }
    public bool ShowCommandLine { get; set; }
    public string MachineName { get; private set; } = Environment.MachineName.ToLower();
    public bool ServerProcessesRunning => runningServers.Count > 0;
    public bool SteamcmdPrettyPrint { get; private set; }

    public Launcher()
    {
      launcherFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
      if (launcherFolder.EndsWith(@"\bin\Debug"))
        launcherFolder = Path.GetDirectoryName(Path.GetDirectoryName(launcherFolder));
    }
    
    #region ReadServerConfig()
    public bool ReadServerConfig()
    {
      // rename ServerConfig.ini template to MyServerConfig.ini to prevent overwriting the user config with an update of the launcher
      var myConfigFile = Path.Combine(launcherFolder, "MyServerConfig.ini");
      var configFile = Path.Combine(launcherFolder, "ServerConfig.ini");
      if (!File.Exists(myConfigFile))
        File.Move(configFile, myConfigFile);
      configFile = myConfigFile;
      MainIni = new IniFile(configFile);

      // import old TOXIKK server config file format if necessary
      if (!MainIni.Sections.Any(s => s.Name.StartsWith(ServerSectionPrefix)))
      {
        Utils.WriteLine("No [" + ServerSectionPrefix + "...] sections found in ServerConfig.ini. Importing settings from ServerConfigList.ini ...");
        ConvertLegacyServerConfigListIni();
        MainIni = new IniFile(configFile);
      }

      // build dictionary for "simple name" translation
      var section = MainIni.GetSection("SimpleNames");
      if (section != null)
      {
        foreach (var key in section.Keys)
          simpleNames[key] = section.GetString(key) ?? "";
      }

      // process [Hosts] section with mappings from physical host name(s) to logical host name
      if ((section = MainIni.GetSection("Hosts")) != null)
      {
        foreach (var key in section.Keys)
        {
          var values = section.GetString(key).ToLower().Replace(" ", "").Split(',');
          if (values.Contains(MachineName))
          {
            MachineName = key;
            break;
          }
        }
      }
      Utils.WriteLine("Using host config for ^B" + MachineName);


      // process launcher config (first found value wins)
      foreach (var sec in GetApplicableSections("ServerLauncher", true))
        ReadLauncherConfig(sec);

      return InitFolders();
    }
    #endregion

    public void SetGlobalVariable(string variable, string op, string value)
    {
      ProcessVariableDefinition(globalVariables, op, variable, value);
    }

    #region ReadLauncherConfig()
    private void ReadLauncherConfig(IniFile.Section section)
    {
      var steamcmdDir = section.GetString("SteamcmdDir");
      if (steamcmdDir != null && this.SteamcmdExe == null)
      {
        var exe = Path.Combine(steamcmdDir, "steamcmd.exe");
        if (File.Exists(exe))
          this.SteamcmdExe = exe;
      }

      this.SteamcmdPrettyPrint = section.GetBool("SteamcmdPrettyPrint");

      var workshopDir = section.GetString("WorkshopDir");
      if (workshopDir != null && this.WorkshopFolder == null && Directory.Exists(workshopDir))
        this.WorkshopFolder = workshopDir;

      var toxikkDir = section.GetString("ToxikkDir");
      if (!string.IsNullOrEmpty(toxikkDir) && this.ToxikkFolder == null)
      {
        var path = Path.Combine(toxikkDir, "Binaries", "Win32", "TOXIKK.exe");
        if (File.Exists(path))
          this.ToxikkFolder = toxikkDir;
        else
          Utils.WriteLine("^EWARNING:^7 ignoring bad ToxikkDir in MyServerConfig.ini: " + path);
      }

      if (this.HttpFolder == null)
        this.HttpFolder = section.GetString("HttpRedirectDir");


      // parse @varname@=value lines
      foreach (var key in section.Keys)
      {
        foreach (var entry in section.GetAll(key))
        {
          if (key.Length >= 3 && key.StartsWith("@") && key.EndsWith("@"))
            ProcessVariableDefinition(this.globalVariables, entry.Operator, key, entry.Value);
        }
      }
    }
    #endregion

    #region ConvertLegacyServerConfigListIni()
    public void ConvertLegacyServerConfigListIni()
    {
      var oldConfigFile = Path.Combine(ToxikkFolder, @"TOXIKKServers", "TOXIKKServerLauncher", "ServerConfigList.ini");
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
        var fields = line.Split(new [] { ' ', '\t' }, 14).Where(f => f != "").Select(f => f.Trim('"')).ToList();
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
        sb.AppendLine($"Mutators={fields[13].Replace(' ',',')}");

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

      if (this.ToxikkFolder == null)
      {
        if (File.Exists(Path.Combine(this.launcherFolder, @"..", "Binaries", "Win32", "TOXIKK.exe")))
          this.ToxikkFolder = Path.Combine(this.launcherFolder, "..");
        else if (this.SteamcmdExe != null)
          this.ToxikkFolder = Path.Combine(Path.GetDirectoryName(this.SteamcmdExe), "SteamApps", "common", "TOXIKK");
      }
      ToxikkFolder = ToxikkFolder?.TrimEnd('\\', '/');
      ToxikkExe = ToxikkFolder == null ? null : Path.Combine(ToxikkFolder, "Binaries", "Win32", "TOXIKK.exe");
      if (ToxikkExe == null || !File.Exists(ToxikkExe))
      {
        Utils.WriteLine("Couldn't find TOXIKK.exe. Please configure ToxikkDir in MyServerConfig.ini or copy+run the launcher from TOXIKK\\TOXIKKServers.");
        return false;
      }

      ConfigFolder = Path.Combine(ToxikkFolder, "UDKGame", "Config");

      if (this.WorkshopFolder == null)
      {
        if (this.SteamcmdExe != null)
          this.WorkshopFolder = Path.Combine(Path.GetDirectoryName(this.SteamcmdExe), "SteamApps", "workshop", "content", "324810");
        else if (this.ToxikkFolder != null)
          this.WorkshopFolder = Path.Combine(this.ToxikkFolder, "..", "..", "workshop", "content", "324810");
      }

      // ReSharper restore PossibleNullReferenceException
      // ReSharper restore AssignNullToNotNullAttribute

      if (!string.IsNullOrEmpty(HttpFolder) && !Directory.Exists(HttpFolder))
        Directory.CreateDirectory(HttpFolder);

      // set some global variables which can be used inside @CopyFile statements
      this.globalVariables["@LauncherDir@"] = this.launcherFolder;
      this.globalVariables["@ToxikkDir@"] = this.ToxikkFolder;
      this.globalVariables["@WorkshopDir@"] = this.WorkshopFolder;
      this.globalVariables["@HttpRedirectDir@"] = this.HttpFolder;

      // process @Copy commands in [ServerLauncher]
      foreach (var sec in GetApplicableSections("ServerLauncher", true))
      {
        foreach (var entry in sec.GetAll("@Copy"))
        {
          var value = ProcessValueMacros(ToxikkFolder, entry.Value, globalVariables, false);
          ProcessCopyFile(ToxikkFolder, launcherFolder, value);
        }
      }

      return true;
    }
    #endregion

    #region FindRunningServers()
    public void FindRunningServers()
    {
      this.runningServers.Clear();
      var prefix = ServerSectionPrefix.ToLower();
      foreach (var sec in this.MainIni.Sections)
      {
        if (!sec.Name.ToLower().StartsWith(prefix))
          continue;
        var id = sec.Name.Substring(ServerSectionPrefix.Length);
        var proc = GetServerProcess(id, false);
        if (proc != null)
          this.runningServers.Add(id);
      }
    }
    #endregion

    #region GetServerProcess()
    private Process GetServerProcess(string id, bool showError)
    {
      var pidFile = Path.Combine(this.ConfigFolder, ServerSectionPrefix + id, "toxikk.pid");
      if (File.Exists(pidFile))
      {
        var txt = File.ReadAllText(pidFile);
        int pid;
        if (int.TryParse(txt, out pid))
        {
          try
          {
            var proc = Process.GetProcessById(pid);
            if (proc.MainModule.FileName.ToLower() == ToxikkExe.ToLower())
              return proc;
          }
          catch
          {
            File.Delete(pidFile);
          }
        }
      }
      if (showError)
        Utils.WriteLine($"^EWARNING:^7 Could not find process for server {id}");
      return null;
    }
    #endregion


    #region StopServer()
    public bool StopServer(string id)
    {     
      var proc = GetServerProcess(id, true);
      if (proc == null)
        return true;
      
      // I tried various methods to simulate Ctrl-C, but none worked (i.e. AttachConsole)
      // CloseMainWindow() below or sending WM_CLOSE terminate the server without a clean shutdown

      var closed = proc.CloseMainWindow() && proc.WaitForExit(3000);
      if (closed)
        this.runningServers.Remove(id);
      else
        Utils.WriteLine("Failed to stop server " + id);
      return closed;
    }
    #endregion

    #region RestartServer()
    public void RestartServer(string id, Workshop workshop = null)
    {      
      var thread = new Thread(x =>
      {
        if (StopServer(id))
        {
          workshop?.UpdateWorkshop(false, true, true);
          StartServer(id);
        }
      });
      thread.Name = id;
      thread.IsBackground = false; // force main process to wait for completion before exitting
      thread.Start();
    }
    #endregion

    #region FocusServerConsole()
    public void FocusServerConsole(string id)
    {
      var proc = GetServerProcess(id, true);
      if (proc == null)
        return;
      var hWnd = proc.MainWindowHandle;
      Win32.ShowWindow(hWnd, Win32.SW_SHOWNORMAL);
      Win32.SetForegroundWindow(hWnd);
      Win32.SetCapture(hWnd);
      Win32.SetFocus(hWnd);
      Win32.SetActiveWindow(hWnd);
    }

    #endregion


    #region CopyFolder()
    public void CopyFolder(string sourceDir, string targetDir)
    {
      if (Verbose)
        Utils.WriteLine("Copying " + sourceDir + " => " + targetDir);

      // ReSharper disable AssignNullToNotNullAttribute
      foreach (var file in Directory.GetFiles(sourceDir))
      {
        // copy files to toxikk/udkgame/workshop/....
        var target = Path.Combine(targetDir, Path.GetFileName(file));
        if (File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(target) || new FileInfo(file).Length != new FileInfo(target).Length)
          FileCopy(file, target, true);

        // copy files to HTTP redirect
        if (!string.IsNullOrEmpty(HttpFolder) && ".udk.upk.u".Contains(Path.GetExtension(file)))
        {
          target = Path.Combine(HttpFolder, Path.GetFileName(file));
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

    #region GetConfigurations()
    public List<ServerInfo> GetConfigurations()
    {
      var list = new List<ServerInfo>();
      if (MainIni.GetSection(ClientSection) != null)
        list.Add(new ServerInfo("0", "Update base configuration and start client", false));
      foreach (var section in MainIni.Sections)
      {
        if (section.Name.StartsWith(ServerSectionPrefix) && !section.Name.Contains(":"))
        {
          var name = section.GetString("@ServerName") ?? section.GetString("ServerName");
          name = UrlDecode(name ?? section.Name);
          name = ProcessValueMacros("", name, this.globalVariables);
          var id = section.Name.Substring(ServerSectionPrefix.Length);
          list.Add(new ServerInfo(id, name, this.runningServers.Contains(id)));
        }
      }
      return list;
    }

    // ServerName may include _ for spaces and $xx to escape special characters as hex codes
    // The server must be running the MutatH0r.CRZMutator_ServerDescription mutator to support this
    private string UrlDecode(string input)
    {
      const string HexDigits = "0123456789ABCDEF";

      var sb = new StringBuilder();
      int i;
      for (i = 0; i < input.Length - 2; i++)
      {
        var c = input[i];
        if (c == '_')
          c = ' ';
        else if (c == '$')
        {
          var d1 = HexDigits.IndexOf(input[i + 1]);
          var d2 = HexDigits.IndexOf(input[i + 2]);
          if (d1 >= 0 && d2 >= 0)
          {
            sb.Append((char) (d1*16 + d2));
            i += 2;
            continue;
          }
        }
        sb.Append(c);
      }
      sb.Append(input.Substring(i));
      return sb.ToString();
    }
    #endregion

    #region StartServer()
    /// <summary>
    /// Before calling this method, make sure to call FindRunningServers() to prevent starting 2 instances of the same server
    /// </summary>
    public bool StartServer(string serverId)
    {
      if (serverId.Trim() == "")
        return false;

      var sectionName = serverId == "0" ? ClientSection : ServerSectionPrefix + serverId;
      var section = MainIni.GetSection(sectionName);
      if (section == null)
      {
        Utils.WriteLine("^CERROR:^7No configuration section for " + sectionName);
        return false;
      }

      if (serverId == "0")
        this.Server = false;

      if (this.GetServerProcess(serverId, false) != null)
      {
        Utils.WriteLine($"^EWARNING:^7 Server with ID {serverId} is already running");
        return false;
      }


      this.globalVariables["@cmdOrId@"] = serverId;
      this.globalVariables["@host@"] = MachineName;

      var name = section.GetString("@ServerName") ?? ProcessValueMacros("", section.GetString("ServerName"), globalVariables) ?? sectionName;
      Utils.WriteLine("\nStarting " + name);
      if (!GenerateConfig(MainIni, section, out var map, out var options, out var cmdArgs, out var varDict))
        return false;

      if (serverId == "0")
        Process.Start("steam://rungameid/324810");
      else
        LaunchServer(map, options, cmdArgs, sectionName, varDict);
      return true;
    }

    #endregion

    #region GenerateConfig()

    public void GenerateConfig(int id)
    {
      var sec = id == 0 ? ClientSection : ServerSectionPrefix + id;
      this.Server = id != 0;
      GenerateConfig(this.MainIni, this.MainIni.GetSection(sec), out _, out _, out _, out _);
    }

    private bool GenerateConfig(IniFile iniFile, IniFile.Section section, out string map, out string options, out string cmdArgs, out Dictionary<string,string> variableDict)
    {
      var targetConfigFolder = this.Server ? Path.Combine(ConfigFolder, section.Name) : this.ConfigFolder.TrimEnd('\\', '/');
      CopyIniFilesToServerConfigFolder(targetConfigFolder, section);

      var destIniCache = new Dictionary<string, IniFile>();
      var optionDict = new SortedDictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);
      variableDict = new Dictionary<string, string>(globalVariables, StringComparer.InvariantCultureIgnoreCase);
      variableDict["@ConfigDir@"] = targetConfigFolder;

      if (this.Lan)
        optionDict["bIsLanMatch"] = "true";

      // default command line args, can be modified with @cmdline =, +=, -=
      cmdArgs = Server ? "-configsubdir=" + section.Name + " -nohomedir -unattended" : "-log -nostartupmovies";
      map = null;

      // recursive processing of a section and its @Import sections, then override everything with values from a more machine specific sections
      foreach(var sec in GetApplicableSections(section.Name))
        ProcessConfigSection(targetConfigFolder, "", iniFile, sec, destIniCache, optionDict, variableDict, ref cmdArgs);

      // build URL with map name and options
      if (!section.Name.StartsWith(ClientSection) && !optionDict.TryGetValue("map", out map))
      {
        Utils.WriteLine("^CERROR:^7 No map specified");
        options = null;
        return false;
      }

      optionDict.Remove("map");
      var sbOptions = new StringBuilder();
      foreach (var entry in optionDict)
      {
        sbOptions.Append("?").Append(entry.Key);
        if (entry.Value != null)
          sbOptions.Append("=").Append(entry.Value.Replace(' ', '_'));
      }

      // save the ini files
      foreach (var destIni in destIniCache.Values)
        destIni.Save();

      // correct timestamps inside UDK*.ini files so they match the corresponding Default*.ini file timestamps 
      // with respect to the broken UE3 daylight saving offset of -1/0/+1 hours
      var fixer = new IniFixer(targetConfigFolder);
      fixer.FixTimestamps(true);

      options = sbOptions.ToString();
      return true;
    }
    #endregion

    #region CopyIniFilesToServerConfigFolder()
    private void CopyIniFilesToServerConfigFolder(string targetConfigFolder, IniFile.Section section)
    {
      if (this.Server)
      {
        // delete all files from the target config folder, except those matching a @Keep=... pattern
        if (Directory.Exists(targetConfigFolder))
          ClearDirectory(targetConfigFolder, GetFilesToKeep(section));

        // copy all Default*.ini files
        foreach (var file in Directory.GetFiles(ConfigFolder, "Default*.ini"))
          FileCopy(file, Path.Combine(targetConfigFolder, Path.GetFileName(file) ?? ""), true);

        // copy UDK*.ini where there is no matching Default*.ini
        // (sometimes UDK* files are accessed before they have been generated from Default* files)
        foreach (var file in Directory.GetFiles(ConfigFolder, "UDK*.ini"))
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

      if (this.Server)
      {
        // copy all *.ini files from Workshop/Config folder (but don't overwrite existing files)
        var dir = Path.Combine(this.ToxikkFolder, "UDKGame", "Workshop", "Config");
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
          var operation = rawValue.Operator; // =, += or -=
          var loopInfo = ProcessCrossProductLoop(rawValue.Value, targetConfigFolder, variables);

          // if a @loop uses the "=" operator, treat it as a "!=" resetting the list and then "+=" to add items
          if (loopInfo.IsLoop && operation == "=")
          {
            ProcessSetting(targetConfigFolder, configSourceFolder, iniFile, destIniCache, options, variables, ref cmdArgs, null, unmappedKey, "!=", null);
            operation = "+=";
          }

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

            var value = ProcessValueMacros(targetConfigFolder, loopInfo.Template, variables);
            ProcessSetting(targetConfigFolder, configSourceFolder, iniFile, destIniCache, options, variables, ref cmdArgs, rawValue, unmappedKey, operation, value);
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
      if (!rawValue.ToLower().StartsWith("@loop"))
        return new LoopInfo(rawValue);

      rawValue = ProcessValueMacros(targetConfigFolder, rawValue, variables, false);

      int startOfTemplate;
      string sep;
      var loops = ExtractLoopList(rawValue, out startOfTemplate, out sep);
      if (loops.Count == 0)
      {
        Utils.WriteLine($"^CWARNING: bad @loop statement: {rawValue}");
        return new LoopInfo();
      }

      var combinationCount = 1;
      List<string[]> loopVals = new List<string[]>(loops.Count);
      foreach (var loop in loops)
      {
        var vals = SplitUnquoted(loop, sep[0]);
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
      return new LoopInfo(rawValue.Substring(startOfTemplate), result);
    }

    /// <summary>
    /// Extract the text enclosed by {...} until we find a ':'
    /// </summary>
    private List<string> ExtractLoopList(string rawValue, out int startOfTemplate, out string sep)
    {
      List<string> loops = new List<string>();
      startOfTemplate = 0;
      int i = rawValue.IndexOf('{');
      sep = rawValue.Substring(5, i - 5).Trim();
      if (sep == "")
        sep = ",";
      for (var len = rawValue.Length; i < len;)
      {
        if (rawValue[i] == ':' && sep != ":") // old syntax required a colon between the last {...} and the template
        {
          startOfTemplate = i + 1;
          break;
        }
        if (char.IsWhiteSpace(rawValue[i]))
        {
          ++i;
          continue;
        }
        if (rawValue[i] == '{')
        {
          int close = rawValue.IndexOf('}', i);
          if (close < 0)
          {
            startOfTemplate = 0;
            loops.Clear();
            break;
          }
          loops.Add(rawValue.Substring(i + 1, close - (i + 1)));
          i = close + 1;
        }
        else
        {
          // new syntax doesn't require a colon. the template can start right after the last {...}
          startOfTemplate = i;
          break;
        }
      }

      return loops;
    }

    #endregion

    #region ProcessSetting()
    private void ProcessSetting(string targetConfigFolder, string configSourceFolder, IniFile iniFile, Dictionary<string, IniFile> destIniCache,
      SortedDictionary<string, string> options, Dictionary<string, string> variables, ref string cmdArgs,
      IniFile.Entry rawValue, string unmappedKey, string operation, string value)
    {
      if (unmappedKey.StartsWith("@"))
      {
        var lowerKey = unmappedKey.ToLower();
        if (lowerKey == "@import")
          ProcessImport(targetConfigFolder, configSourceFolder, iniFile, destIniCache, options, variables, ref cmdArgs, value);
        else if (lowerKey == "@copy" || lowerKey == "@copyfiles") // @copyfiles is legacy name
          ProcessCopyFile(targetConfigFolder, configSourceFolder, value);
        else if (lowerKey == "@cmdline")
          ProcessCommandLineArg(ref cmdArgs, operation, value);
        else if (lowerKey == "@steamsockets")
          this.Steamsockets = (value ?? "").ToLower() == "true" || (value ?? "").ToLower() == "1";
        else if (lowerKey == "@seekfreeloading")
          this.Seekfreeloading = (value ?? "").ToLower() == "true" || (value ?? "").ToLower() == "1";
        else if (lowerKey.Length >= 3 && lowerKey.EndsWith("@"))
          ProcessVariableDefinition(variables, operation, lowerKey, value);
        else if (lowerKey == "@servername" || lowerKey == "@keep")
        {
        }
        else
          Utils.WriteLine($"^EWARNING: ignoring unknown directive: {unmappedKey}={rawValue.Value}");
      }
      else
      {
        string mappedKey;
        if (!simpleNames.TryGetValue(unmappedKey, out mappedKey))
          mappedKey = unmappedKey;

        var configMapping = mappedKey.Split('\\');
        if (configMapping.Length == 3)
          ProcessIniSetting(targetConfigFolder, destIniCache, operation, configMapping, value);
        else
          ProcessUrlParameter(options, operation, mappedKey, value);
      }
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
        if (!variables.TryGetValue(varName, out varValue) && !GetSettingValue(varName, out varValue))
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

      // @sc,skillclass-value
      match = skillClassRegex.Match(value);
      if (match.Success)
      {
        int sc = int.Parse(match.Groups[1].Value);
        sc = Math.Min(12, Math.Max(1, sc));
        return SkillClassDifficulty[sc].ToString();
      }

      // @env,environment-variable-name
      if (value.StartsWith("@env,", StringComparison.InvariantCultureIgnoreCase))
        return Environment.GetEnvironmentVariable(value.Substring(4).Trim()) ?? "";
      
      return value;
    }

    private bool GetSettingValue(string name, out string value)
    {
      value = "";
      return false;
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
      else if (operation == "?=")
      {
        if (!destSec.Keys.Contains(configMapping[2]) || destSec.GetAll(configMapping[2]).Count == 0)
          destSec.Set(configMapping[2], value);
      }
      else if (operation == ":=" || operation == "!=")
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
            Utils.WriteLine($"^EWARNING: @import={value}: failed to locate {subFolder}{subFile}\\{subSection}");
          else
          {
            ProcessConfigSection(targetConfigFolder, subFolder, subIni, sec, destIniCache, options, variables, ref cmdArgs);
            ProcessConfigSection(targetConfigFolder, subFolder, subIni, subIni.GetSection(subSection + ":" + MachineName), destIniCache, options, variables, ref cmdArgs);
          }
        }
        else // import section from same file
        {
          var secs = GetApplicableSections(import);
          if (secs.Count == 0)
            Utils.WriteLine($"^EWARNING: @import={value}: failed to locate [{import}]");
          else
          {
            foreach(var sec in secs)
              ProcessConfigSection(targetConfigFolder, configSourceFolder, iniFile, sec, destIniCache, options, variables, ref cmdArgs);
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
          var folder = File.Exists(Path.Combine(launcherFolder, configSourceFolder, sourceName)) ? Path.Combine(launcherFolder, configSourceFolder) : ConfigFolder;
          var source = Path.Combine(folder, sourceName);
          if (File.Exists(source))
            FileCopy(source, Path.Combine(targetConfigFolder, destName), true);
          else
            Utils.WriteLine($"^EWARNING: @copy source not found: {sourceName}");
        }
      }
    }
    #endregion

    #region ProcessCommandLineArg()
    private static void ProcessCommandLineArg(ref string cmdArgs, string operation, string value)
    {
      if (operation == "=")
        cmdArgs = value;
      else if (operation == "?=")
      {
        if (cmdArgs == "")
          cmdArgs = value;
      }
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
    private void ProcessVariableDefinition(IDictionary<string, string> variables, string op, string varName, string value)
    {
      value = ProcessValueMacros("", value, this.globalVariables);
      ProcessUrlParameter(variables, op, varName, value); // reuse the code, but that has nothing to do with URL parameters
    }
    #endregion

    #region ProcessUrlParameter()
    private static void ProcessUrlParameter(IDictionary<string, string> options, string operation, string mappedKey, string fullValue)
    {
      if (operation == "")
        options[mappedKey] = null;
      else if (operation == ":=" || operation == "!=")
        options.Remove(mappedKey);
      else if (operation == "=")
        options[mappedKey] = fullValue;
      else if (operation == "?=")
      {
        if (!options.ContainsKey(mappedKey) || options[mappedKey] == "")
          options[mappedKey] = fullValue;
      }
      else
      {
        IEnumerable<string> values = SplitUnquoted(fullValue, ',');
        if (operation == "*=") // prepend multiple values, but keep the values in the specified order
          values = values.Reverse();

        foreach (var value in values)
        {
          string oldValue;
          options.TryGetValue(mappedKey, out oldValue);

          if (operation == ".=")
            options[mappedKey] = string.IsNullOrEmpty(oldValue) ? value : oldValue + "," + value;
          else if (operation == "+=")
          {
            if (oldValue == null || !("," + oldValue + ",").Contains("," + value + ","))
              options[mappedKey] = string.IsNullOrEmpty(oldValue) ? value : oldValue + "," + value;
          }
          else if (operation == "*=")
          {
            if (oldValue == null || !("," + oldValue + ",").Contains("," + value + ","))
              options[mappedKey] = string.IsNullOrEmpty(oldValue) ? value : value + "," + oldValue;
          }
          else if (operation == "-=")
          {
            if (oldValue != null)
            {
              oldValue = "," + oldValue + ",";
              options[mappedKey] = oldValue.Replace("," + value + ",", ",").Replace(",,", ",").Trim(',');
            }
          }
        }
      }
    }

    #endregion

    #region LaunchServer()
    private void LaunchServer(string map, string options, string cmdArgs, string sectionName, Dictionary<string, string> variables)
    {
      string args = "";
      if (Server)
        args = "server ";

      args += map;

      if (Dedicated)
        args += "?dedicated=true";
      else
        args += "?listen=true";

      args += options;

      if (Steamsockets && !Lan)
        args += "?steamsockets";

      if (this.Seekfreeloading)
        cmdArgs += " -seekfreeloading";

      if (cmdArgs.Length > 0)
        args += " " + cmdArgs;

      if (this.ShowCommandLine)
        Utils.WriteLine("INFO: starting " + ToxikkExe + " " + args);

      Environment.CurrentDirectory = Path.GetDirectoryName(ToxikkExe) ?? "";
      try
      {
        string exe = ToxikkExe;
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
          exe = "wineconsole";
          args = "\"" + ToxikkExe + "\" " + args;
        }

        // set environment variables for the server
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        foreach (var entry in variables)
        {
          if (entry.Key.StartsWith("@env."))
            psi.EnvironmentVariables[entry.Key.Substring(5).TrimEnd('@')]=entry.Value;
        }
       
        var proc = Process.Start(psi);
        if (proc == null)
          Utils.WriteLine("Couldn't start TOXIKK for " + sectionName);
        else
        {
          var pidFile = Path.Combine(ConfigFolder, sectionName, "toxikk.pid");
          File.WriteAllText(pidFile, proc.Id.ToString());
        }
      }
      catch (Exception ex)
      {
        Utils.WriteLine("^CERROR: failed to launch ^A" + ToxikkExe + "^7: \n" + ex.Message);
      }
    }
    #endregion

    #region SplitUnquoted()
    private static string[] SplitUnquoted(string input, char separator)
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


    #region GetApplicableSections()
    internal List<IniFile.Section> GetApplicableSections(string sectionName, bool mostSpecificFirst = false)
    {
      sectionName = sectionName.ToLower();
      var list = new List<IniFile.Section>();
      foreach (var sec in MainIni.Sections)
      {
        if (IsSectionApplicableToCurrentMachine(sec, sectionName))
          list.Add(sec);
      }

      // sort list by generic, machine not excluded, machine specific - or reverse if mostSpecificFirst is true
      list.Sort((s1, s2) =>
      {
        var c1 = s1.Name.IndexOf(':') < 0 ? -1 : s1.Name.IndexOf('!') < 0 ? 1 : 0;
        var c2 = s2.Name.IndexOf(':') < 0 ? -1 : s2.Name.IndexOf('!') < 0 ? 1 : 0;
        return c1.CompareTo(c2)*(mostSpecificFirst ? -1 : 1);
      });
      return list;
    }
    #endregion

    #region IsSectionApplicableToCurrentMachine()
    private bool IsSectionApplicableToCurrentMachine(IniFile.Section sec, string sectionName)
    {
      var name = sec.Name.ToLower();
      if (name == sectionName)
        return true;

      if (!name.StartsWith(sectionName + ":"))
        return false;

      var machines = name.Substring(name.IndexOf(":") + 1).Replace(" ", "");
      if (machines[0] == '!')
        return !machines.Substring(1).Split(',').Contains(MachineName);

      return machines.Split(',').Contains(MachineName);
    }

    #endregion
  }

  #region class LoopInfo
  class LoopInfo
  {
    public readonly bool IsLoop;
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
      IsLoop = true;
      Template = loopTemplate;
      CombinationValues = combinationValues;
    }
  }
  #endregion

  #region class ServerInfo
  public class ServerInfo
  {
    public string ID { get; }
    public string Name { get; }
    public bool IsRunning { get; }

    public ServerInfo(string id, string descr, bool isRunning)
    {
      ID = id;
      Name = descr;
      IsRunning = isRunning;
    }
  }
  #endregion
}

