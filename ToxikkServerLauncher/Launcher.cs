using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ToxikkServerLauncher
{
  class Launcher
  {
    private const string ServerSectionPrefix = "DedicatedServer";
    private string steamcmdExe;
    private string launcherFolder;
    private string toxikkFolder;
    private string configFolder;
    private string workshopFolder;
    private string httpFolder;
    private string toxikkExe;
    private IniFile ini;
    private bool dedicated = true;
    private bool showCommandLine;
    private bool pause;
    private bool skipWorkshopUpdate;
    private bool forceWorkshopUpdate;

    /// <summary>
    /// Maps logical server setting names to real setting names (either ini-file\section\section specifier or a command line option name) as found in the [SimpleNames] section
    /// </summary>
    private readonly Dictionary<string,string> keyMapping = new Dictionary<string, string>();

    #region Run()
    public void Run(string[] args)
    {
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

      // generate server config(s) and start the server(s)
      foreach (var id in serverIds)
        RunServerConfiguration(id);
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
            case "showcommand":
              this.showCommandLine = true;
              break;
            case "pause":
            case "p":
              this.pause = true;
              break;
            case "skipupdate":
            case "s":
              this.skipWorkshopUpdate = true;
              break;
            case "forceupdate":
            case "f":
              this.forceWorkshopUpdate = true;
              break;
          }
        }
        else
          serverIds.Add(arg);
      }

      return serverIds;
    }

    #endregion

    #region ReadServerConfig()
    private void ReadServerConfig()
    {
      var myConfigFile = Path.Combine(launcherFolder, "MyServerConfig.ini");
      var configFile = Path.Combine(launcherFolder, "ServerConfig.ini");
      if (!File.Exists(myConfigFile))
        File.Move(configFile, myConfigFile);
      configFile = myConfigFile;
      ini = new IniFile(configFile);

      // import old server config file format if necessary
      if (!ini.Sections.Any(s => s.Name.StartsWith(ServerSectionPrefix)))
      {
        Console.WriteLine("No [" + ServerSectionPrefix + "...] sections found in ServerConfig.ini. Importing settings from ServerConfigList.ini ...");
        ConvertLegacyServerConfigListIni();
        ini = new IniFile(configFile);
      }

      // build dictionary for "simple name" translation
      var section = ini.GetSection("SimpleNames");
      foreach (var key in section.Keys)
        keyMapping[key] = section.GetString(key) ?? "";

      // process launcher config 
      section = ini.GetSection("ServerLauncher");
      if (section != null)
      {
        var toxikkDir = section.GetString("ToxikkDir");
        if (toxikkDir != null && this.toxikkFolder == null)
        {
          var path = Path.Combine(toxikkDir, @"Binaries\win32\TOXIKK.exe");
          if (File.Exists(path))
            this.toxikkFolder = toxikkDir;
        }

        var workshopDir = section.GetString("WorkshopDir");
        if (workshopDir != null && this.workshopFolder == null && Directory.Exists(workshopDir))
          this.workshopFolder = workshopDir;

        this.httpFolder = section.GetString("HttpRedirectDir");

        var steamcmdDir = section.GetString("SteamcmdDir");
        if (steamcmdDir != null)
        {
          var exe = Path.Combine(steamcmdDir, "steamcmd.exe");
          if (File.Exists(exe))
            this.steamcmdExe = exe;
        }
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
      toxikkFolder = toxikkFolder?.TrimEnd('\\', '/') ?? Path.Combine(launcherFolder, "..");
      toxikkExe = Path.Combine(toxikkFolder, @"Binaries\win32\TOXIKK.exe");
      configFolder = Path.Combine(toxikkFolder, @"UDKGame\Config");

      if (workshopFolder == null)
      {
        // ReSharper disable PossibleNullReferenceException
        // ReSharper disable AssignNullToNotNullAttribute
        workshopFolder = Path.GetDirectoryName(toxikkFolder);
        while (Path.GetFileName(workshopFolder).ToLower() != "steamapps")
          workshopFolder = Path.GetDirectoryName(workshopFolder);
        workshopFolder = Path.Combine(workshopFolder, @"workshop\content\324810\");
        // ReSharper restore PossibleNullReferenceException
        // ReSharper restore AssignNullToNotNullAttribute
      }

      if (httpFolder != null && !Directory.Exists(httpFolder))
        Directory.CreateDirectory(httpFolder);

      if (!File.Exists(toxikkExe))
      {
        Console.Error.WriteLine("Could not find TOXIKK.exe.\n" +
          @"Either copy this program to SteamApps\Common\TOXIKK\TOXIKKServers," +
          "\nset ToxikkDir in ServerConfig.ini/[ServerLauncher]\n" +
          "\nor use the /toxikkdir=... command line parameter");
        return false;
      }

      return true;
    }

    #endregion

    #region UpdateWorkshop()
    private void UpdateWorkshop()
    {
      if (System.Diagnostics.Process.GetProcessesByName("toxikk").Length >= 1)
        Console.WriteLine("TOXIKK.exe is already running, skipping workshop updates.");
      else
      {
        DownloadWorkshopItems();
        Console.WriteLine("Copying workshop item contents to TOXIKK and HTTP redirect folders...");
        CopyWorkshopContent();
      }
    }
    #endregion

    #region DownloadWorkshopItems()
    private void DownloadWorkshopItems()
    {
      if (this.skipWorkshopUpdate)
        return;

      if (this.steamcmdExe == null)
      {
        Console.WriteLine("Steamcmd not configured, skipping workshop updates.");
        return;
      }

      var sec = ini.GetSection("SteamWorkshop");
      if (sec == null)
        return;

      var items = sec.GetAll("Item");
      if (items.Count == 0)
        return;

      var user = sec.GetString("User");
      var pass = sec.GetString("Password");
      if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
      {
        Console.WriteLine("User/Password not configured in [SteamWorkshop], skipping workshop updates.");
        return;
      }

      // delete the manifest file to make sure we really download
      if (this.forceWorkshopUpdate)
        File.Delete(Path.Combine(this.workshopFolder, @"..\..\appworkshop_324810.acf"));

      var sb = new StringBuilder();
      sb.Append("+login ").Append(user).Append(" ").Append(pass).Append(" +app_update 324810");
      foreach (var item in items)
        sb.Append(" +workshop_download_item 324810 ").Append(item.Value);
      sb.Append(" +quit");

      Console.WriteLine("Updating TOXIKK and Steam Workshop Items...\n");
      var psi = new System.Diagnostics.ProcessStartInfo(this.steamcmdExe, sb.ToString());
      psi.UseShellExecute = false;
      var proc = System.Diagnostics.Process.Start(psi);
      proc?.WaitForExit();
      Console.WriteLine("\nSteam update complete.\n");
    }
    #endregion

    #region CopyWorkshopContent()
    private void CopyWorkshopContent()
    {
      if (!Directory.Exists(workshopFolder))
        return;

      var toxikkDir = Path.Combine(this.toxikkFolder, @"UDKGame\Workshop");
      try
      {
        // delete existing files so only content of the workshop items listed in the .ini survives
        if (Directory.Exists(toxikkDir))
          Directory.Delete(toxikkDir, true);
        foreach (var itemPath in Directory.GetDirectories(workshopFolder))
          CopyFolder(itemPath, toxikkDir);
      }
      catch (IOException ex)
      {
        Console.Error.WriteLine("Failed to copy workshop item: " + ex.Message);
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
        if (httpFolder != null && ".udk.upk.u".Contains(Path.GetExtension(file)))
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
      Console.WriteLine("Available server configurations:");
      foreach (var section in ini.Sections)
      {
        if (section.Name.StartsWith(ServerSectionPrefix))
        {
          var name = section.GetString("@ServerName") ?? section.GetString("ServerName");
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

      var sectionName = ServerSectionPrefix + serverId;
      var section = ini.GetSection(sectionName);
      if (section == null)
      {
        Console.Error.WriteLine("No configuration section for " + sectionName);
        return;
      }

      Console.WriteLine("Starting " + (section.GetString("ServerName") ?? sectionName));
      string map, options, cmdArgs;
      if (GenerateConfig(section, out map, out options, out cmdArgs))
        LaunchServer(map, options, cmdArgs, sectionName);
    }

    #endregion

    #region GenerateConfig()
    private bool GenerateConfig(IniFile.Section section, out string map, out string options, out string cmdArgs)
    {
      var targetConfigFolder = this.dedicated ? Path.Combine(configFolder, section.Name) : this.configFolder.TrimEnd('\\', '/');
      CopyIniFilesToServerConfigFolder(targetConfigFolder);

      var destIniCache = new Dictionary<string, IniFile>();
      var optionDict = new SortedDictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);

      // default command line args, can be modified with @cmdline =, +=, -=
      cmdArgs = dedicated ? "-configsubdir=" + section.Name + " -nohomedir -unattended" : "-log -nostartupmovies";

      // recursive processing of a section and its @Import sections
      ProcessConfigSection(targetConfigFolder, "", section, destIniCache, optionDict, ref cmdArgs);

      // build URL with map name and options
      if (!optionDict.TryGetValue("map", out map))
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
    private void CopyIniFilesToServerConfigFolder(string targetConfigFolder)
    {
      if (this.dedicated)
      {
        if (Directory.Exists(targetConfigFolder))
          Directory.Delete(targetConfigFolder, true);

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
    private void ProcessConfigSection(string targetConfigFolder, string configSourceFolder, IniFile.Section section, Dictionary<string, IniFile> destIniCache, SortedDictionary<string,string> options, ref string cmdArgs)
    {
      foreach (var unmappedKey in section.Keys)
      {
        foreach (var rawValue in section.GetAll(unmappedKey))
        {
          var value = rawValue.Value;
          var operation = rawValue.Operator; // =, += or -=

          value = ProcessValueMacros(targetConfigFolder, value);

          string mappedKey;
          if (!keyMapping.TryGetValue(unmappedKey, out mappedKey))
            mappedKey = unmappedKey;

          var configMapping = mappedKey.Split('\\');
          if (configMapping.Length == 3)
            ProcessIniSetting(targetConfigFolder, destIniCache, operation, configMapping, value);
          else if (mappedKey.ToLower() == "@import")
            ProcessImport(targetConfigFolder, configSourceFolder, destIniCache, options, ref cmdArgs, value);
          else if (mappedKey.ToLower() == "@copyfiles")
            ProcessCopyFile(targetConfigFolder, configSourceFolder, value);
          else if (mappedKey.ToLower() == "@cmdline")
            ProcessCommandLineArg(ref cmdArgs, operation, value);
          else if (!mappedKey.StartsWith("@"))
            ProcessUrlParameter(options, operation, mappedKey, value);
        }
      }
    }
    #endregion

    #region ProcessValueMacros()
    private static string ProcessValueMacros(string targetConfigFolder, string value)
    {
      var portRegex = new Regex(@"^@port,(\d+),(\d+)\w*$");
      var serverNumRegx = new Regex(@".*?(\d+)$");

      // process @port,base,multiplier macro to auto-generate port numbers
      var port = portRegex.Match(value);
      var serv = serverNumRegx.Match(targetConfigFolder);
      if (port.Success && serv.Success)
      {
        value = (int.Parse(port.Groups[1].Value) + int.Parse(port.Groups[2].Value)*(int.Parse(serv.Groups[1].Value) - 1)).ToString();
      }
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
      string destIniPath = targetConfigFolder + "\\" + configMapping[0];
      if (!destIniCache.TryGetValue(destIniPath, out destIni))
      {
        destIni = new IniFile(destIniPath);
        destIniCache.Add(destIniPath, destIni);
      }
      var destSec = destIni.GetSection(configMapping[1], true);
      if (operation == "=")
        destSec.Set(configMapping[2], value);
      else if (operation == "+=")
        destSec.Add(configMapping[2], value);
      else if (operation == "-=")
        destSec.Remove(configMapping[2], value);
    }

    #endregion

    #region ProcessImport()
    /// <summary>
    /// recursively process settings from another section or file
    /// </summary>
    private void ProcessImport(string targetConfigFolder, string configSourceFolder, Dictionary<string, IniFile> destIniCache, SortedDictionary<string, string> options, ref string cmdArgs, string value)
    {
      var importRegex = new Regex(@"^(?:(.*)[/\\])?(\S+\.ini)(\\\S+)?$", RegexOptions.IgnoreCase);

      foreach (var import in value.Split(','))
      {
        var match = importRegex.Match(import);
        if (match.Success)
        {
          var subFolder = match.Groups[1].Success ? Path.Combine(configSourceFolder, match.Groups[1].Value) : configSourceFolder;
          var subFile = match.Groups[2].Value;
          var subSection = match.Groups[3].Success ? match.Groups[3].Value.Substring(1) : Path.GetFileName(targetConfigFolder);
          var subIni = new IniFile(Path.Combine(subFolder, subFile));
          var sec = subIni.GetSection(subSection);
          if (sec == null)
            Console.Error.WriteLine("WARNING: @import={0}: failed to locate {1}{2}{3}", value, subFolder, subFile, subSection);
          else
            ProcessConfigSection(targetConfigFolder, subFolder, sec, destIniCache, options, ref cmdArgs);
        }
        else
          ProcessConfigSection(targetConfigFolder, configSourceFolder, ini.GetSection(import), destIniCache, options, ref cmdArgs);
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
      foreach (var fileInfo in value.Split(','))
      {
        var names = fileInfo.Split('=', ':', '\\', '/'); // various separator chars to prevent exploits with absolute paths
        if (names.Length == 2 && names[0] != "" && names[1] != "")
        {
          var folder = File.Exists(Path.Combine(launcherFolder, configSourceFolder, names[0])) ? Path.Combine(launcherFolder, configSourceFolder) : configFolder;
          var source = Path.Combine(folder, names[0]);
          if (File.Exists(source))
            FileCopy(source, Path.Combine(targetConfigFolder, names[1]), true);
          else
            Console.Error.WriteLine("WARNING: @copyfile source not found: " + names[0]);
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
        cmdArgs += " " + value;
      else if (operation == "-=")
        cmdArgs = cmdArgs.Replace(value, "").Replace("  ", " ").Trim();
    }
    #endregion

    #region ProcessUrlParameter()
    private static void ProcessUrlParameter(SortedDictionary<string, string> options, string operation, string mappedKey, string value)
    {
      if (operation == "=")
        options[mappedKey] = value;
      else if (operation == "+=")
      {
        string oldValue;
        options[mappedKey] = options.TryGetValue(mappedKey, out oldValue) ? oldValue + "," + value : value;
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

      args += "?steamsockets";

      if (cmdArgs.Length > 0)
        args += " " + cmdArgs;

      if (this.showCommandLine)
        Console.WriteLine(toxikkExe + " " + args);

      Environment.CurrentDirectory = Path.GetDirectoryName(toxikkExe) ?? "";
      System.Diagnostics.Process.Start(toxikkExe, args);

      if (this.pause)
        Console.ReadLine();
    }
    #endregion

  }
}
