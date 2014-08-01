using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Configuration;
using System.IO;
using System.Linq;
using NLog;

namespace CgmImport
{
    class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
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
                    if(fileInfo != null)
                        fileInfo.IsRandomized = true;
                    
                    //if already imported then skip
                    if (subjectImportInfo.IsCgmImported)
                    {
                        Console.WriteLine("Subject already imported: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    //check if completed - if not then skip
                    if (! subjectImportInfo.SubjectCompleted)
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
                    if (! cgmFileInfo.IsRandomized)
                    {
                        Console.WriteLine("CGM file is not randomized: " + cgmFileInfo.SubjectId);
                        notificationList.Add("CGM file is not randomized: " + cgmFileInfo.FileName);
                    }

                    if (cgmFileInfo.IsImportable)
                    {
                        if (IsValidFile(cgmFileInfo))
                        {
                            
                        }
                        else
                        {
                            notificationList.Add("CGM file is not a valid format: " + cgmFileInfo.FileName);
                        }
                    }
                }
            }


            Console.Read();
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
                        if (! line.Contains("PatientInfoField	PatientInfoValue"))
                        {
                            Console.WriteLine("***Invalid file: " + fullFileName);
                            Console.WriteLine(line);
                            return false;
                        }
                        else
                        {
                            Console.WriteLine("Valid file: " + fullFileName);
                        }
                    }
                }
            }


            return true;
        }

        private static void GetFirstLastChecksDates(int studyId)
        {
            
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

                        pos = rdr.GetOrdinal("ChecksImportCompleted");
                        ci.ImportCompleted = !rdr.IsDBNull(pos) && rdr.GetBoolean(pos);

                        pos = rdr.GetOrdinal("IsCgmImported");
                        ci.IsCgmImported = !rdr.IsDBNull(pos) && rdr.GetBoolean(pos);

                        pos = rdr.GetOrdinal("ChecksRowsCompleted");
                        ci.RowsCompleted = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

                        pos = rdr.GetOrdinal("ChecksLastRowImported");
                        ci.LastRowImported = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

                        pos = rdr.GetOrdinal("DateCompleted");
                        ci.SubjectCompleted = !rdr.IsDBNull(pos);

                        pos = rdr.GetOrdinal("ChecksHistoryLastDateImported");
                        ci.HistoryLastDateImported = !rdr.IsDBNull(pos) ? (DateTime?)rdr.GetDateTime(pos) : null;

                        pos = rdr.GetOrdinal("ChecksCommentsLastRowImported");
                        ci.CommentsLastRowImported = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

                        pos = rdr.GetOrdinal("ChecksSensorLastRowImported");
                        ci.SensorLastRowImported = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

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
        public DateTime? FirstChecksSendorDateTime { get; set; }
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
        public bool ImportCompleted { get; set; }
        public bool SubjectCompleted { get; set; }

        public bool IsCgmImported { get; set; }
        public int RowsCompleted { get; set; }
        public int LastRowImported { get; set; }
        public DateTime? HistoryLastDateImported { get; set; }
        public int CommentsLastRowImported { get; set; }
        public int SensorLastRowImported { get; set; }
       
        public List<EmailNotification> EmailNotifications { get; set; }
        
    }

    public class EmailNotification
    {
        public string Message { get; set; }

    }
}
