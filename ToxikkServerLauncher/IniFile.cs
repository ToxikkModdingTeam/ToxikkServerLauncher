using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ToxikkServerLauncher
{
  public class IniFile
  {
    #region class Entry

    public class Entry
    {
      public string Value;
      public string Operator;

      public Entry(string val, string op = "=")
      {
        Value = val;
        Operator = op;
      }

      public override string ToString()
      {
        return Value;
      }
    }

    #endregion

    #region class Section

    public class Section
    {
      private readonly Dictionary<string, List<Entry>> data = new Dictionary<string, List<Entry>>(StringComparer.CurrentCultureIgnoreCase);

      public Section(string name)
      {
        this.Name = name;
      }

      #region Name
      public string Name { get; private set; }
      #endregion

      #region Add()
      internal void Add(string key, string value, string op = "=")
      {
        this.Insert(key, value, data.Count, op);
      }
      #endregion

      #region Insert()
      internal void Insert(string key, string value, int index, string op = "=")
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list))
        {
          list = new List<Entry>();
          data.Add(key, list);
        }
        list.Add(new Entry(value, op));
      }
      #endregion


      #region Remove()
      internal void Remove(string key, string value)
      {
        List<Entry> list;
        if (data.TryGetValue(key, out list))
        {
          for (int i = 0; i < list.Count; i++)
          {
            if (list[i].Value == value)
            {
              list.RemoveAt(i);
              --i;
            }
          }
        }          
      }
      #endregion

      #region Set()
      internal void Set(string key, string value, string op = "=")
      {
        data[key] = new List<Entry> { new Entry(value, op) };
      }
      #endregion

      #region Keys
      public IEnumerable<string> Keys => data.Keys;

      #endregion

      #region GetString()
      public string GetString(string key)
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list))
          return null;
        return list[0].Value;
      }
      #endregion

      #region GetBool()
      public bool GetBool(string key, bool defaultValue = false)
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list) || list.Count == 0)
          return defaultValue;
        var val = list[0].Value.ToLower();
        if (val == "")
          return defaultValue;
        return val != "0" && val != "false";
      }
      #endregion

      #region GetInt()
      public int GetInt(string key, int defaultValue = 0)
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list) || list.Count == 0)
          return defaultValue;
        var val = list[0].Value.ToLower();
        if (val == "")
          return defaultValue;
        int intVal;
        return Int32.TryParse(val, out intVal) ? intVal : defaultValue;
      }
      #endregion

      #region GetDecimal()
      public decimal GetDecimal(string key, decimal defaultValue = 0)
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list) || list.Count == 0)
          return defaultValue;
        var val = list[0].Value.ToLower();
        if (val == "")
          return defaultValue;
        decimal intVal;
        return Decimal.TryParse(val, out intVal) ? intVal : defaultValue;
      }
      #endregion


      #region GetAll()

      public List<Entry> GetAll(string key)
      {
        List<Entry> list;
        if (!data.TryGetValue(key, out list))
          return new List<Entry>();
        return list;
      }

      #endregion

      #region ToString()
      public override string ToString()
      {
        return "[" + Name + "]";
      }
      #endregion
    }
    #endregion

    private readonly Dictionary<string, Section> sectionDict;
    private readonly List<Section> sectionList;
    private readonly string fileName;

    #region ctor()
    public IniFile(string fileName)
    {
      this.sectionDict = new Dictionary<string, Section>();
      this.sectionList = new List<Section>();
      this.fileName = fileName;
      this.ReadIniFile();
    }
    #endregion

    public IEnumerable<Section> Sections => this.sectionList;

    public string FileName => this.fileName;

    #region GetSection()
    public Section GetSection(string sectionName, bool create = false)
    {
      Section section;
      sectionDict.TryGetValue(sectionName, out section);
      if (section == null && create)
      {
        section = new Section(sectionName);
        sectionList.Add(section);
        sectionDict.Add(sectionName, section);
      }
      return section;
    }
    #endregion

    #region ReadIniFile()
    private void ReadIniFile()
    {
      if (!File.Exists(fileName))
        return;
      using (StreamReader rdr = new StreamReader(fileName))
      {
        Section currentSection = null;
        string line;
        string key = null;
        string val = null;
        string op = null;
        while ((line = rdr.ReadLine()) != null)
        {
          string trimmedLine = line.Trim();
          if (trimmedLine.StartsWith(";"))
            continue;

          if (trimmedLine.StartsWith("["))
          {
            string sectionName = trimmedLine.EndsWith("]") ? trimmedLine.Substring(1, trimmedLine.Length - 2) : trimmedLine.Substring(1);
            currentSection = this.GetSection(sectionName, true); // merge multiple sections with same name
            this.sectionDict[sectionName] = currentSection;
            continue;
          }

          if (currentSection == null)
            continue;

          int idx = -1;
          if (val == null) // assignment starts on this line (not a continuation from a previous line which ended with a backslash)
          {
            idx = trimmedLine.IndexOf("=");
            if (idx <= 0)
              continue;

            if ("+-*".IndexOf(trimmedLine[idx - 1]) >= 0)
            {
              op = trimmedLine[idx - 1] + "=";
              key = trimmedLine.Substring(0, idx - 1).Trim();
            }
            else
            {
              op = "=";
              key = trimmedLine.Substring(0, idx).Trim();
            }
            val = "";
          }

          if (line.EndsWith("\\")) // value will continue on the next line
            val += line.Substring(idx + 1, line.Length - idx - 1 - 1).Trim() + "\n";
          else // complete value available
          {
            val += line.Substring(idx + 1).Trim();
            currentSection.Add(key, val, op);
            val = null;
          }
        }
      }
    }
    #endregion

    #region Save()
    public void Save()
    {
      var sb = new StringBuilder();
      foreach (var section in this.sectionList)
      {
        sb.Append("[").Append(section.Name).AppendLine("]");
        foreach (var key in section.Keys)
        {
          foreach (var value in section.GetAll(key))
            sb.AppendLine($"{key}{value.Operator}{value.Value}");
        }
        sb.AppendLine();
      }
      File.WriteAllText(this.fileName, sb.ToString());
    }
    #endregion

    #region ToString()
    public override string ToString()
    {
      return FileName;
    }
    #endregion
  }
}
