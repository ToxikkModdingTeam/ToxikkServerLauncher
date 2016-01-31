using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

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

      if (System.Diagnostics.Process.GetProcessesByName("toxikk").Length >= 1)
        Console.WriteLine("TOXIKK.exe is already running, skipping workshop updates.");
      else
      {

        DownloadWorkshopItems();
        Console.WriteLine("Copying workshop item contents to TOXIKK and HTTP redirect folders...");
        CopyWorkshopContent();
      }

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
              this.dedicated = false;
              break;
            case "showcommand":
              this.showCommandLine = true;
              break;
            case "pause":
              this.pause = true;
              break;
            case "skipupdate":
              this.skipWorkshopUpdate = true;
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

    #region InitFolders()
    private bool InitFolders()
    {
      toxikkFolder = toxikkFolder?.TrimEnd('\\', '/') ?? Path.Combine(launcherFolder,  "..");
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

    #region ListConfigurations()
    private void ListConfigurations()
    {
      Console.WriteLine("Available server configurations:");
      foreach (var section in ini.Sections)
      {
        if (section.Name.StartsWith(ServerSectionPrefix))
          Console.WriteLine($"{section.Name.Substring(ServerSectionPrefix.Length),3}: {section.GetString("ServerName")}");
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
      string map, options;
      if (GenerateConfig(section, out map, out options))
        LaunchServer(map, options, sectionName);
    }

    #endregion

    #region GenerateConfig()
    private bool GenerateConfig(IniFile.Section section, out string map, out string options)
    {
      string targetConfigFolder;
      if (this.dedicated)
      {
        targetConfigFolder = configFolder + section.Name;
        Directory.CreateDirectory(targetConfigFolder);

        // copy all Default*.ini files
        foreach (var file in Directory.GetFiles(configFolder, "Default*.ini"))
          File.Copy(file, Path.Combine(targetConfigFolder, Path.GetFileName(file)??""), true);

        // copy UDK*.ini where there is no matching Default*.ini
        // (sometimes UDK* files are accessed before they have been generated from Default* files)
        foreach (var file in Directory.GetFiles(configFolder, "UDK*.ini"))
        {
          var fileName = Path.GetFileName(file) ?? "";
          var defaultFile = Path.Combine(Path.GetDirectoryName(file)??"", "Default" + fileName.Substring(3));
          if (!File.Exists(defaultFile))
            File.Copy(file, Path.Combine(targetConfigFolder, fileName), true);
        }
      }
      else
        targetConfigFolder = this.configFolder.TrimEnd('\\', '/');

      // copy all UDK*.ini files from the ServerLauncher folder
      foreach (var file in Directory.GetFiles(launcherFolder, "UDK*.ini"))
      {
        var fileName = Path.GetFileName(file) ?? "";
        File.Copy(file, Path.Combine(targetConfigFolder, fileName), true);
      }

      var destIniCache = new Dictionary<string, IniFile>();
      var optionDict = new SortedDictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);

      // recursive processing of a section and its @Import sections
      ProcessConfigSection(targetConfigFolder, section, destIniCache, optionDict);

      // build URL with map and options
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

    #region ProcessConfigSection()
    private void ProcessConfigSection(string targetConfigFolder, IniFile.Section section, Dictionary<string, IniFile> destIniCache, SortedDictionary<string,string> options)
    {
      foreach (var unmappedKey in section.Keys)
      {
        var value = section.GetString(unmappedKey);
        string mappedKey;
        if (!keyMapping.TryGetValue(unmappedKey, out mappedKey))
          mappedKey = unmappedKey;

        var configMapping = mappedKey.Split('\\');
        if (configMapping.Length == 3)
        {
          // process a "filename\section\key=value" entry
          IniFile destIni;
          string destIniPath = targetConfigFolder + "\\" + configMapping[0];
          if (!destIniCache.TryGetValue(destIniPath, out destIni))
          {
            destIni = new IniFile(destIniPath);
            destIniCache.Add(destIniPath, destIni);
          }
          var destSec = destIni.GetSection(configMapping[1], true);
          destSec.Set(configMapping[2], value);
        }        
        else if (mappedKey.ToLower() == "@import")
        {
          // recursively process settings from another section
          ProcessConfigSection(targetConfigFolder, ini.GetSection(value), destIniCache, options);
        }
        else if (mappedKey.ToLower() == "@copyfiles")
        {
          // copy files specified as "source:dest,source:dest,...". 
          // source files can be placed in the ServerLauncher folder or the template config folder
          foreach (var fileInfo in value.Split(','))
          {
            var names = fileInfo.Split('=', ':', '\\', '/'); // various separator chars to prevent exploits with absolute paths
            if (names.Length == 2 && names[0] != "" && names[1] != "")
            {
              var folder = File.Exists(Path.Combine(launcherFolder, names[0])) ? launcherFolder : configFolder;
              var source = Path.Combine(folder, names[0]);
              if (File.Exists(source))
                File.Copy(Path.Combine(folder, names[0]), Path.Combine(targetConfigFolder, names[1]), true);
              else
                Console.Error.WriteLine("WARNING: @copyfile source not found: " + names[0]);
            }
          }
        }
        else
        {
          // everything else is appended as ?key=value to the UE3 URL
          options[mappedKey] = value;
        }
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

      var sb = new StringBuilder();
      sb.Append("+login ").Append(user).Append(" ").Append(pass).Append(" +app_update 324810");
      foreach (var item in items)
        sb.Append(" +workshop_download_item 324810 ").Append(item);
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
    private bool CopyWorkshopContent()
    {
      if (!Directory.Exists(workshopFolder))
        return true;

      var toxikkDir = Path.Combine(this.toxikkFolder, @"UDKGame\Workshop");
      try
      {
        foreach (var itemPath in Directory.GetDirectories(workshopFolder))
          CopyFolder(itemPath, toxikkDir);
        return true;
      }
      catch (IOException ex)
      {
        Console.Error.WriteLine("Failed to copy workshop item: " + ex.Message);
        return false;
      }
    }
    #endregion

    #region CopyFolder()
    private void CopyFolder(string sourceDir, string targetDir)
    {
      if (!Directory.Exists(targetDir))
        Directory.CreateDirectory(targetDir);

      // ReSharper disable AssignNullToNotNullAttribute
      foreach (var file in Directory.GetFiles(sourceDir))
      {
        // copy files to toxikk/udkgame/workshop/....
        var target = Path.Combine(targetDir, Path.GetFileName(file));
        if (File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(target) || new FileInfo(file).Length != new FileInfo(target).Length)
          File.Copy(file, target, true);

        // copy files to HTTP redirect
        if (httpFolder != null)
        {
          target = Path.Combine(httpFolder, Path.GetFileName(file));
          if (File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(target) || new FileInfo(file).Length != new FileInfo(target).Length)
            File.Copy(file, target, true);
        }
      }
      
      // recursively process subdirectories
      foreach (var dir in Directory.GetDirectories(sourceDir))
        CopyFolder(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
      // ReSharper restore AssignNullToNotNullAttribute
    }
    #endregion

    #region LaunchServer()
    private void LaunchServer(string map, string options, string sectionName)
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

      if (dedicated)
        args += " -configsubdir=" + sectionName + " -nohomedir -unattended";
      else
        args += " -log -nostartupmovies";

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
