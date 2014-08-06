using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;

namespace CgmImport
{
    class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        //private static List<DbColumn> _dbColList = new List<DbColumn>(); 
        static void Main(string[] args)
        {
            Logger.Info("Starting CGM Import Service");

            //get sites and load into list of siteInfo 
            var sites = GetSites();
            
            //iterate sites
            foreach (var si in sites)
            {
                Console.WriteLine("Site: " + si.Name);
                //get site randomized studies - return list of ChecksImportInfo
                var randList = GetRandimizedStudies(si.Id);

                //get the list of uploaded checks files in upload directory
                var cgmFileList = GetCgmFileInfos(si.Name);

                //iterate randomized list
                foreach (var subjectImportInfo in randList)
                {
                    //check for uploaded file
                    var fileInfo = cgmFileList.Find(x => x.SubjectId == subjectImportInfo.SubjectId);
                    if (fileInfo != null)
                        fileInfo.IsRandomized = true;

                    //if already imported then skip
                    if (subjectImportInfo.IsCgmImported)
                    {
                        Console.WriteLine("Subject already imported: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    //check if completed - if not then skip
                    if (!subjectImportInfo.SubjectCompleted)
                    {
                        Console.WriteLine("Subject not completed: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    if (fileInfo == null)
                    {
                        var emailNote = new EmailNotification { Message = "CGM file not uploaded." };
                        subjectImportInfo.EmailNotifications.Add(emailNote);
                        Console.WriteLine("Upload file not found: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    fileInfo.IsImportable = true;
                    Console.WriteLine("Subject is importable: " + subjectImportInfo.SubjectId);

                } //end of foreach (var subjectImportInfo in randList)

                //iterate file list
                //get list of files not on randomized list
                var notificationList = new List<string>();
                foreach (var cgmFileInfo in cgmFileList)
                {
                    if (cgmFileInfo.SubjectId == "18-0008-4")
                    {
                        var s = true;
                    }
                    if (!cgmFileInfo.IsRandomized)
                    {
                        Console.WriteLine("CGM file is not randomized: " + cgmFileInfo.SubjectId);
                        notificationList.Add("CGM file is not randomized: " + cgmFileInfo.FileName);
                        continue;
                    }

                    if (cgmFileInfo.IsImportable)
                    {
                        if (!IsValidFile(cgmFileInfo))
                        {
                            Console.WriteLine("CGM file is not a valid format: " + cgmFileInfo.FileName);
                            notificationList.Add("CGM file is not a valid format: " + cgmFileInfo.FileName);
                            continue;
                        }

                        var dbRows = ParseFile(cgmFileInfo);
                        if (! (dbRows.Count > 0))
                        {
                            Console.WriteLine("CGM file has no rows: " + cgmFileInfo.FileName);
                            notificationList.Add("CGM file has no rows: " + cgmFileInfo.FileName);
                            continue;
                        }

                        var subjRandInfo = randList.Find(x => x.SubjectId == cgmFileInfo.SubjectId);
                        string message;
                        if (!IsValidDateRange(dbRows, cgmFileInfo, subjRandInfo, out message))
                        {
                            notificationList.Add(message);
                            Console.WriteLine(message);
                            continue;
                        }
                        if (message.Length > 0)
                        {
                            Console.WriteLine(message);
                        }
                    }
                }
            }


            Console.Read();
        }

        private static bool IsValidDateRange(List<DbRow> dbRows, CgmFileInfo cgmFileInfo, SubjectImportInfo subjectImportInfo, out string message)
        {
            var firstCgmGlucoseDate = GetFirstCgmGlucoseDate(dbRows);
            if (firstCgmGlucoseDate == null)
            {
                message = "***Invalid date range: Could not get the first glucose date from file:" + cgmFileInfo.SubjectId;
                return false;
            }

            var lastCgmGlucoseDate = GetLastCgmGlucoseDate(dbRows);
            if (lastCgmGlucoseDate == null)
            {
                message = "***Invalid date range: Could not get the last glucose date from file:" + cgmFileInfo.SubjectId;
                return false;
            }

            //get checks first and last entries for subject
            GetFirstLastChecksSensorDates(cgmFileInfo, subjectImportInfo.StudyId);
            if((cgmFileInfo.FirstChecksSensorDateTime == null || cgmFileInfo.LastChecksSensorDateTime ==null))
            {
                message = "***Invalid date range: Could not get checks first and last glucose entry dates:" + cgmFileInfo.SubjectId;
                return false;
            }


            if ((cgmFileInfo.FirstChecksSensorDateTime.Value.Date.CompareTo(firstCgmGlucoseDate.Value.Date) > 0))
            {
                message = "***Invalid date range: The first sensor date(" + firstCgmGlucoseDate.Value.Date.ToShortDateString() + 
                    ") is earlier than the first checks date(" +
                    cgmFileInfo.FirstChecksSensorDateTime.Value.ToShortDateString() + 
                    "):" + cgmFileInfo.SubjectId;
                return false;
            }

            if ((cgmFileInfo.LastChecksSensorDateTime.Value.Date.CompareTo(lastCgmGlucoseDate.Value.Date) < 0))
            {
                message = "***Invalid date range: The last sensor date(" + lastCgmGlucoseDate.Value.Date.ToShortDateString() + 
                    ") is later than the last checks date(" + 
                    cgmFileInfo.LastChecksSensorDateTime.Value.ToShortDateString() +  
                    "):" + cgmFileInfo.SubjectId;
                return false;
            }
            message = "Valid date range:" + cgmFileInfo.SubjectId;
            return true;
        }

        private static DateTime? GetFirstCgmGlucoseDate(IEnumerable<DbRow> dbRows)
        {
            //try getting the date from the first row
            foreach (var dbRow in dbRows)
            {
                var datenv = dbRow.ColNameVals.Find(x => x.Name == "GlucoseInternalTime");
                var date = GetDateFromNameValue(datenv.Value);
                if (date != null)
                    return date;

            }
            return null;
        }

        private static DateTime? GetLastCgmGlucoseDate(List<DbRow> dbRows)
        {
            //try getting the date from the first row
            for(var i=dbRows.Count -1; i>0; i--)
            {
                var dbRow = dbRows[i];
                var datenv = dbRow.ColNameVals.Find(x => x.Name == "GlucoseInternalTime");
                var date = GetDateFromNameValue(datenv.Value);
                if (date != null)
                    return date;

            }
            return null;
        }
        private static DateTime? GetDateFromNameValue(string value )
        {
            DateTime date;
            if (DateTime.TryParse(value, out date))
                return date;
            return null;
        }
        private static List<DbRow> ParseFile(CgmFileInfo cgmFileInfo)
        {
            var dbRows = new List<DbRow>();
            using (var sr = new StreamReader(cgmFileInfo.FullName))
            {
                var rows = 0;
                string line;
                string[] colNameList = { };
                
                while ((line = sr.ReadLine()) != null)
                {
                    var columns = line.Split('\t');
                    //first row contains the column names
                    if (rows == 0)
                    {
                        colNameList = (string[])columns.Clone();
                        rows++;
                        continue;
                    }

                    var dbRow = new DbRow();
                    //skip the first two columns - don't need - that's why i starts at 2
                    for (int i = 2; i < columns.Length - 1; i++)
                    {
                        var col = columns[i];
                        var colName = colNameList[i];
                        
                        var colValName = new DbColNameVal {Name = colName, Value = col};

                        dbRow.ColNameVals.Add(colValName);
                    }
                    dbRows.Add(dbRow);
                }
            }
            return dbRows;
        }
        
        private static bool IsValidFile(CgmFileInfo cgmFileInfo)
        {
            var fullFileName = cgmFileInfo.FullName;
            using (var sr = new StreamReader(fullFileName))
            {
                if (sr.Peek() >= 0)
                {
                    //Reads the line, splits on tab and adds the components to the table
                    var line = sr.ReadLine();
                    if (line != null)
                    {
                        if (!line.Contains("PatientInfoField	PatientInfoValue"))
                        {
                            Console.WriteLine("***Invalid file: " + fullFileName);
                            Console.WriteLine(line);
                            return false;
                        }
                    }
                }
            }

            Console.WriteLine("Valid file: " + fullFileName);
            return true;
        }

        private static void GetFirstLastChecksSensorDates(CgmFileInfo cgmFileInfo, int studyId)
        {
            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetChecksFirstAndLastSensorDateTime" };
                    var param = new SqlParameter("@studyId", studyId);
                    cmd.Parameters.Add(param);

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    if (rdr.Read())
                    {
                        var pos = rdr.GetOrdinal("firstDate");
                        if (! rdr.IsDBNull(pos))
                        {
                            cgmFileInfo.FirstChecksSensorDateTime = rdr.GetDateTime(pos);
                        }

                        pos = rdr.GetOrdinal("lastDate");
                        if (!rdr.IsDBNull(pos))
                        {
                            cgmFileInfo.LastChecksSensorDateTime = rdr.GetDateTime(pos);
                        }
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }
        }

        private static IEnumerable<SiteInfo> GetSites()
        {
            var sil = new List<SiteInfo>();

            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetSitesActive" };

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var si = new SiteInfo();
                        var pos = rdr.GetOrdinal("ID");
                        si.Id = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("Name");
                        si.Name = rdr.GetString(pos);

                        pos = rdr.GetOrdinal("SiteID");
                        si.SiteId = rdr.GetString(pos);

                        //pos = rdr.GetOrdinal("LastNovanetFileDateImported");
                        //si.LastFileDate = rdr.IsDBNull(pos) ? (DateTime?)null : rdr.GetDateTime(pos);

                        sil.Add(si);
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }
            return sil;
        }

        private static List<SubjectImportInfo> GetRandimizedStudies(int site)
        {
            var list = new List<SubjectImportInfo>();

            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetRandomizedStudiesForImportForSite" };

                    var param = new SqlParameter("@siteID", site);
                    cmd.Parameters.Add(param);

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var ci = new SubjectImportInfo { SiteId = site };

                        var pos = rdr.GetOrdinal("ID");
                        ci.RandomizeId = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("SubjectId");
                        ci.SubjectId = rdr.GetString(pos).Trim();

                        pos = rdr.GetOrdinal("StudyId");
                        ci.StudyId = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("Arm");
                        ci.Arm = rdr.GetString(pos);

                        pos = rdr.GetOrdinal("IsCgmImported");
                        ci.IsCgmImported = !rdr.IsDBNull(pos) && rdr.GetBoolean(pos);
                        
                        pos = rdr.GetOrdinal("ChecksLastRowImported");
                        ci.LastRowImported = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

                        pos = rdr.GetOrdinal("DateCompleted");
                        if (!rdr.IsDBNull(pos))
                        {
                            ci.DateCompleted = rdr.GetDateTime(pos);
                            ci.SubjectCompleted = true;
                        }

                        pos = rdr.GetOrdinal("DateRandomized");
                        if (! rdr.IsDBNull(pos))
                            ci.DateRandomized = rdr.GetDateTime(pos);
                        
                        pos = rdr.GetOrdinal("SiteName");
                        ci.SiteName = rdr.GetString(pos);

                        list.Add(ci);
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }

            return list;
        }

        private static List<CgmFileInfo> GetCgmFileInfos(string siteName)
        {
            var list = new List<CgmFileInfo>();

            var folderPath = ConfigurationManager.AppSettings["CgmUploadPath"];
            var path = Path.Combine(folderPath, siteName);

            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);

                FileInfo[] fis = di.GetFiles();

                list.AddRange(fis.OrderBy(f => f.Name).Select(fi => new CgmFileInfo
                {
                    FileName = fi.Name,
                    FullName = fi.FullName,
                    SubjectId = fi.Name.Replace("_CGM.csv", ""),
                    IsRandomized = false
                }));
            }
            return list;
        }
    }

    public class SiteInfo
    {
        public int Id { get; set; }
        public string SiteId { get; set; }
        public string Name { get; set; }

    }

    public class CgmFileInfo
    {
        public string FileName { get; set; }
        public string FullName { get; set; }
        public string SubjectId { get; set; }
       
        public bool IsRandomized { get; set; }
        public bool IsValidFile { get; set; }
        public string InvalidReason { get; set; }
        public bool IsImportable { get; set; }
        public DateTime? FirstChecksSensorDateTime { get; set; }
        public DateTime? LastChecksSensorDateTime { get; set; }
    }

    public class SubjectImportInfo
    {
        public SubjectImportInfo()
        {
            EmailNotifications = new List<EmailNotification>();
        }
        public int RandomizeId { get; set; }
        public string Arm { get; set; }
        public string SubjectId { get; set; }
        public int SiteId { get; set; }
        public string SiteName { get; set; }
        public int StudyId { get; set; }
        public bool SubjectCompleted { get; set; }
        public bool IsCgmImported { get; set; }
        public int RowsCompleted { get; set; }
        public int LastRowImported { get; set; }
        public DateTime? DateRandomized { get; set; }
        public DateTime? DateCompleted { get; set; }
       
        public List<EmailNotification> EmailNotifications { get; set; }

    }

    public class EmailNotification
    {
        public string Message { get; set; }

    }

    public class DbRow
    {
        public DbRow()
        {
            ColNameVals = new List<DbColNameVal>();    
        }
        public List<DbColNameVal> ColNameVals { get; set; }
    }

    public class DbColNameVal
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
    
}
