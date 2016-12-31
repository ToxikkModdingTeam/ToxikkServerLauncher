using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;

namespace ToxikkServerLauncher
{
  /// <summary>
  /// This class handles updates of 
  /// - TOXIKK game files through steamcmd
  /// - Steam Workshop items through steamcmd
  /// - non-workshop items through .zip file URLs
  /// - http redirect folder
  /// </summary>
  public class Workshop
  {
    #region class ItemStatus
    private class ItemStatus
    {
      public readonly string FolderName;
      public readonly long WorkshopId;
      public readonly string ZipUrl;
      public readonly bool RequireDownload;

      public ItemStatus(string iniValue, string workshopFolder, bool forceDownload)
      {
        int i = iniValue.IndexOf(";");
        if (i >= 0)
          iniValue = iniValue.Substring(0, i);
        iniValue = iniValue.Trim();
        if (long.TryParse(iniValue, out this.WorkshopId))
          this.FolderName = WorkshopId.ToString();
        else if ((iniValue.StartsWith("http://") || iniValue.StartsWith("https://")) && iniValue.EndsWith(".zip"))
        {
          this.ZipUrl = iniValue;
          this.FolderName = Path.GetFileNameWithoutExtension(new Uri(iniValue).AbsolutePath);
        }
        else
          this.FolderName = iniValue;

        var dir = Path.Combine(workshopFolder, this.FolderName);
        bool isDownloadable = WorkshopId != 0 || !string.IsNullOrEmpty(ZipUrl);
        this.RequireDownload = isDownloadable && (forceDownload || !Directory.Exists(dir) || Directory.GetDirectories(dir).Length == 0);
      }
    }
    #endregion

    private readonly Launcher launcher;

    public Workshop(Launcher launcher)
    {
      this.launcher = launcher;
    }

    #region UpdateToxikk()

    public void UpdateToxikk(bool validate)
    {
      Utils.WriteLine("Updating TOXIKK...\n");

      string cmd = " +app_update 324810";

      foreach (var sec in launcher.GetApplicableSections("SteamWorkshop", true))
      {
        var betaName = sec.GetString("BetaName");
        var betaPass = sec.GetString("BetaPassword");
        if (!string.IsNullOrWhiteSpace(betaName))
        {
          cmd += " -beta " + betaName;
          if (!string.IsNullOrWhiteSpace(betaPass))
            cmd += " -betapassword " + betaPass;
          break;
        }
      }     

      if (validate)
        cmd += " validate";

      RunSteamcmd(cmd, launcher.ToxikkFolder);
    }
    #endregion

    #region CleanWorkshopFolder()
    public void CleanWorkshopFolder()
    {
      // delete the manifest file to make sure we really download
      Utils.WriteLine("Cleaning " + launcher.WorkshopFolder);
      File.Delete(Path.Combine(launcher.WorkshopFolder, @"..\..\appworkshop_324810.acf"));

      // delete all numeric folders (which can be reacquired from steam) but keep alphanumeric folders (with developer content)
      foreach (var itemFolder in Directory.GetDirectories(launcher.WorkshopFolder))
      {
        long dummy;
        if (long.TryParse(Path.GetFileName(itemFolder), out dummy))
        {
          try { Directory.Delete(itemFolder, true); }
          catch (IOException ex)
          {
            Utils.WriteLine("^CERROR:^7 couldn't delete " + itemFolder + ": " + ex.Message);
          }
        }
        else
        {
          Utils.WriteLine("^FINFO:^7 keeping non-steam folder " + itemFolder);
        }
      }
    }

    #endregion

    #region UpdateWorkshop()
    public bool UpdateWorkshop(bool forceUpdate, bool steam, bool zip)
    {
      var itemStatus = CheckItemStatus(forceUpdate);
      if (itemStatus.Count(e => e.RequireDownload) == 0)
        return false;
      
      Directory.CreateDirectory(launcher.WorkshopFolder);
      if (steam)
        DownloadSteamWorkshopItems(itemStatus);
      if (zip)
        DownloadZipItems(itemStatus);
      return true;
    }
    #endregion

    #region DeployWorkshopItems()
    public void DeployWorkshopItems()
    {
      var items = CheckItemStatus(false);
      CopyWorkshopContent(items);
    }
    #endregion

    #region CheckItemStatus()
    private List<ItemStatus> CheckItemStatus(bool forceUpdate)
    {
      var itemList = new List<ItemStatus>();
      foreach (var sec in launcher.GetApplicableSections("SteamWorkshop"))
      {
        var items = sec.GetAll("Item");
        foreach (var item in items)
        {
          if (item.Operator == ":=" || item.Operator == "!=")
          {
            itemList.Clear();
            continue;
          }

          var newStatus = new ItemStatus(item.Value, launcher.WorkshopFolder, forceUpdate);
          var oldStatus = itemList.FirstOrDefault(e => e.FolderName == newStatus.FolderName);
          if (oldStatus != null)
          {
            if (item.Operator == "-=")
              itemList.Remove(oldStatus);
            continue;
          }

          itemList.Add(newStatus);
        }
      }
      return itemList;
    }
    #endregion

    #region DownloadWorkshopItems()

    private void DownloadSteamWorkshopItems(List<ItemStatus> items)
    {
      var todo = items.Where(e => e.RequireDownload && e.WorkshopId != 0).ToList();
      if (todo.Count == 0)
        return;

      var sb = new StringBuilder();
      foreach (var item in todo)
        sb.Append(" +workshop_download_item 324810 ").Append(item.WorkshopId);

      Utils.WriteLine("Updating " + todo.Count + " Steam workshop items...\n");
      var baseFolder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(launcher.WorkshopFolder))) ?? "", "steamapps");
      RunSteamcmd(sb.ToString(), baseFolder);
    }
    #endregion

    #region RunSteamcmd()
    private void RunSteamcmd(string cmd, string forceInstallDir = null)
    { 
      if (launcher.SteamcmdExe == null)
      {
        Utils.WriteLine("^EWARNING:^7 steamcmd.exe not found, skipping updates.");
        return;
      }

      string user, pass;
      if (!GetSteamLogin(out user, out pass))
      {
        Utils.WriteLine("^EWARNING:^7 User not configured in [SteamWorkshop], skipping updates.");
        return;
      }

      var sb = new StringBuilder();
      sb.Append("+login ").Append(user).Append(pass != null ? " " + pass : "");
      if (forceInstallDir != null)
        sb.Append(" +force_install_dir \"").Append(forceInstallDir).Append("\"");
      sb.Append(cmd);
      sb.Append(" +quit");

      // when not providing a password on the command line, steamcmd may prompt for the password or use a cached password
      // in case of a cached password, steamcmd randomly exists with error code 5 when getting license information, so we have some retry logic here
      var psi = new ProcessStartInfo(launcher.SteamcmdExe, sb.ToString());
      psi.UseShellExecute = false;
      psi.RedirectStandardOutput = true;
      int attempt = 1;
      bool retry;
      do
      {
        retry = false;
        var proc = Process.Start(psi);
        if (proc == null)
          Utils.WriteLine("\n^1ERROR:^7 Failed to start steamcmd.exe\n");
        else
        {
          ProcessSteamcmdStdout(proc);
          proc.WaitForExit();

          Utils.WriteLine(proc.ExitCode == 0 ? "\nSteam update complete.\n" : "\n^EWARNING:^7 Steam update completed with exit code " + proc.ExitCode.ToString("x8") + ".\n");
          retry = (proc.ExitCode & 0xFFFF) == 5 && attempt++ <= 5;
        }
      } while (retry);
    }
    #endregion

    #region ProcessSteamcmdStdout()
    private static void ProcessSteamcmdStdout(Process proc)
    {
      // reformat output to have item-id and download status on the same line and get rid of the file paths
      var regex1 = new Regex(@"(Downloaded item \d+ to .*? bytes\) ?)|(\)\.)");
      var regex2 = new Regex(@"Download item \d+ failed \(");

      char[] buffer = new char[1024];
      string s = "";
      int len;
      do
      {
        len = proc.StandardOutput.Read(buffer, 0, buffer.Length);
        if (len == 0)
          s += "\r\n";
        else
          s += new string(buffer, 0, len);
        int i;

        // password-prompt ends without a newline
        i = s.IndexOf("password:");
        if (i >= 0)
        {
          Console.Write(s.Substring(0, i));
          s = s.Substring(i + 9);
          Console.Write("\nPassword: ");
          Console.ForegroundColor = ConsoleColor.Black;
          continue;
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        s = s.Replace("\r", "");
        while ((i = s.IndexOf('\n')) >= 0)
        {
          string line = s.Substring(0, i);
          line = regex2.Replace(line, "");
          line = regex1.Replace(line, "\n");
          s = s.Substring(i + 1);
          Console.Write(line.Replace("\n", "\r\n"));
          if (!line.EndsWith("..."))
            Console.WriteLine();
        }
      } while (len > 0);
    }

    #endregion

    #region GetSteamLogin()
    private bool GetSteamLogin(out string user, out string pass)
    {
      user = pass = null;
      foreach (var sec in launcher.GetApplicableSections("SteamWorkshop", true))
      {
        if (string.IsNullOrWhiteSpace(user))
          user = sec.GetString("User");
        if (string.IsNullOrWhiteSpace(pass))
          pass = sec.GetString("Password");
      }

      if (string.IsNullOrWhiteSpace(user))
        user = null;
      if (user == null)
        return false;

      if (string.IsNullOrWhiteSpace(pass))
        pass = null;

      // remove cached steam password, because that stuff is buggy as hell in steamcmd.exe
      if (pass == null)
      {
        var path = Path.Combine(Path.GetDirectoryName(launcher.SteamcmdExe) ?? "", @"config\config.vdf");
        if (File.Exists(path))
        {
          var txt = File.ReadAllText(path);
          int i = txt.IndexOf("\"ConnectCache\"");
          i = txt.IndexOf("{", i);
          i = txt.IndexOf("\n", i) + 1;
          var j = txt.LastIndexOf('\n', txt.IndexOf("}", i)) + 1;

          txt = txt.Substring(0, i) + txt.Substring(j);
          File.WriteAllText(path, txt);
        }
      }

      return true;
    }

    #endregion

    #region DownloadZipItems()
    private void DownloadZipItems(List<ItemStatus> items)
    {
      var todo = items.Where(e => e.RequireDownload && !string.IsNullOrEmpty(e.ZipUrl)).ToList();
      if (todo.Count == 0)
        return;

#if PARALLEL
      CountdownEvent countdown = new CountdownEvent(1);
      foreach (var item in todo)
      {
        var cli = new WebClient();
        cli.DownloadFileCompleted += OnDownloadZipItemCompleted;

        Utils.WriteLine("Downloading " + item.ZipUrl + " ...");
        countdown.AddCount(1);
        var file = Path.Combine(launcher.WorkshopFolder, item.FolderName + ".zip");
        File.Delete(file);
        cli.DownloadFileAsync(new Uri(item.ZipUrl), file, new Tuple<string, ItemStatus, CountdownEvent>(file, item, countdown));
      }
      countdown.Signal();
      countdown.Wait();
#else
      int i = 0;
      foreach (var item in todo)
      {
        ++i;

        Utils.Write(item.ZipUrl + " (" + i + "/" + todo.Count + ") ... ");

        var dir = Path.Combine(launcher.WorkshopFolder, item.FolderName);

        DateTime remoteDate;
        using (var cli = new HttpClient())
        {
          var req = new HttpRequestMessage();
          req.Method = HttpMethod.Head;
          req.RequestUri = new Uri(item.ZipUrl);
          var task = cli.SendAsync(req);
          if (!task.Wait(1000))
          {
            Utils.WriteLine("^1timeout^7");
            continue;
          }
          remoteDate = GetDateHeader(task.Result);
        }

        var localDate = File.GetLastWriteTimeUtc(dir);
        if (remoteDate != DateTime.MinValue && Directory.Exists(dir) && Math.Abs((remoteDate - localDate).TotalSeconds) < 1)
        {
          Utils.WriteLine("up-to-date");
          continue;
        }

        Utils.WriteLine("downloading");
        var file = dir + ".zip";
        File.Delete(file);
        var wc = new WebClient();
        AsyncCompletedEventArgs args;
        try
        {
          wc.DownloadFile(new Uri(item.ZipUrl), file);
          args = new AsyncCompletedEventArgs(null, false, new Tuple<string, ItemStatus, CountdownEvent, DateTime>(file, item, null, remoteDate));
        }
        catch (Exception ex)
        {
          args = new AsyncCompletedEventArgs(ex, false, new Tuple<string, ItemStatus, CountdownEvent, DateTime>(file, item, null, remoteDate));
        }
        OnDownloadZipItemCompleted(wc, args);
      }
#endif      
    }
    #endregion

    #region GetDateHeader()
    private static DateTime GetDateHeader(HttpResponseMessage msg)
    {
      foreach (var hdr in msg.Content.Headers)
      {
        if (hdr.Key == "Last-Modified")
          return DateTime.ParseExact(((string[]) hdr.Value)[0], "r", CultureInfo.InvariantCulture);
      }
      return DateTime.MinValue;
    }
    #endregion

    #region OnDownloadZipItemCompleted()
    private void OnDownloadZipItemCompleted(object sender, AsyncCompletedEventArgs e)
    {
      var context = (Tuple<string, ItemStatus, CountdownEvent, DateTime>) e.UserState;
      var file = context.Item1;
      var item = context.Item2;
      var countdown = context.Item3;
      var date = context.Item4;

      if (e.Error != null)
        Utils.WriteLine("^CERROR:^7 Failed to download " + item.ZipUrl);
      else
      {
        FastZip zip = new FastZip();
        var dir = Path.Combine(launcher.WorkshopFolder, item.FolderName);
        try
        {
          if (Directory.Exists(dir))
            Directory.Delete(dir, true);
          zip.ExtractZip(file, dir, FastZip.Overwrite.Always, null, null, null, true);

          // if extracting the zip created paths like 324810\MyItem\MyItem, move the subfolders one level up
          var dupePath = Path.Combine(dir, item.FolderName);
          if (Directory.Exists(dupePath))
          {
            foreach (var subDir in Directory.GetFileSystemEntries(dupePath))
            {
              // ReSharper disable AssignNullToNotNullAttribute
              var target = Path.Combine(dir, Path.GetFileName(subDir));
              if (Directory.Exists(subDir))
                Directory.Move(subDir, target);
              else
                File.Move(subDir, target);
              // ReSharper restore AssignNullToNotNullAttribute
            }
            Directory.Delete(dupePath);
          }
          if (date != DateTime.MinValue)
            Directory.SetLastWriteTimeUtc(dir, date);
          File.Delete(file);
        }
        catch (Exception ex)
        {
          Utils.WriteLine("^CERROR:^7 Failed to extract " + file + ": " + ex.Message);
        }
      }
      countdown?.Signal();
      ((WebClient)sender).Dispose();
    }
    #endregion

    #region CopyWorkshopContent()
    private void CopyWorkshopContent(List<ItemStatus> items)
    {
      if (!Directory.Exists(launcher.WorkshopFolder))
        return;

      Utils.WriteLine("Copying workshop item contents to TOXIKK and HTTP redirect folders...");
      if (launcher.ServerProcessesRunning)
        Utils.WriteLine("^EWARNING^7: TOXIKK.exe is already running, updates may fail.");

      if (launcher.HttpFolder == null)
        Utils.WriteLine("^EWARNING:^7 no HTTP redirect folder configured. Clients won't be able to auto-download workshop items.");

      var toxikkWorkshopDir = Path.Combine(launcher.ToxikkFolder, @"UDKGame\Workshop");
      try
      {
        // delete existing files so only content of the workshop items listed in the .ini survives
        if (Directory.Exists(toxikkWorkshopDir))
          Directory.Delete(toxikkWorkshopDir, true);
      }
      catch (IOException ex)
      {
        Utils.WriteLine($"^CERROR:^7 Failed to delete {toxikkWorkshopDir}: {ex.Message}");
      }

      foreach (var item in items)
      {
        var itemPath = Path.Combine(launcher.WorkshopFolder, item.FolderName);
        try
        {
          if (Directory.Exists(itemPath))
            launcher.CopyFolder(itemPath, toxikkWorkshopDir);
          else
            Utils.WriteLine($"^EWARNING:^7 Workshop item folder not found: {itemPath}");
        }
        catch (IOException ex)
        {
          Utils.WriteLine($"^CERROR:^7 Failed to copy workshop item {itemPath}: {ex.Message}");
        }
      }
    }
    #endregion
  }
}
