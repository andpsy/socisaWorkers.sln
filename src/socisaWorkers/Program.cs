using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using SOCISA;
using System.IO;
using Newtonsoft.Json;
using System.Reflection;
using StackExchange.Redis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using System.Dynamic;
using SOCISA.Models;
using Newtonsoft.Json.Linq;
//using MySql.Data.MySqlClient;
//using System.Data;
//using Xfinium.Pdf;
//using ImageMagick;

namespace socisaWorkers
{
    public struct ParametersResponse
    {
        public string DbHost { get; set; }
        public string DbPort { get; set; }
        public string DbDataBase { get; set; }
        public string DbUser { get; set; }
        public string DbPassword { get; set; }
        public string MessageId { get; set; }
        public string CorrelationId { get; set; }
        public string RedisClientId { get; set; }
        public string AuthenticatedUserId { get; set; }
        public string CommandPredicate { get; set; }
        public string CommandObjectRepository { get; set; }
        public string CommandArguments { get; set; }
    }

    public class NewResponse : response
    {
        public string MessageId { get; set; }
        public string CorrelationId { get; set; }
        public string RedisClientId { get; set; }

        public NewResponse(response r)
        {
            this.Result = r.Result;
            this.InsertedId = r.InsertedId;
            this.Error = r.Error;
            this.Message = r.Status ? null : r.Message; // daca status = succes nu mai populam Message-ul - 19.02
        }
    }
    public class Program
    {
        /*
        static string DbHost = "localhost";
        static string DbPort = "6603";
        static string DbDataBase = "";
        static string DbUser = "";
        static string DbPassword = "";
        static string AuthenticatedUserId = "1"; // !!!!! to change after authentication is implemented
        //static string ConnectionString;

        static string CommandPredicate = "";
        static string CommandObjectRepository = "";
        static string CommandArguments = "";
        static string RedisClientId = "";
        static string MessageId = "";
        static string CorrelationId = "";
        */
        static string CommandArgumentsSeparator = "***";
        static string ScansFolder = "scans";
        static string PdfsFolder = "pdf";
        static Dictionary<string, string> FullyQualifiedNames = new Dictionary<string, string>();

        public static void Main(string[] args)
        {
            /* *****************************************************
             * deoarece toate definitile tabelelor se afla in libraria externa "socisaDll" ca sa folosim Activator-ul pt. ele 
             * ne trebuie FullQualifiedName; de aceea cream un dictionary cu numele simplu al tipului, respectiv qualified name-ul
             * ***************************************************** */
            try
            {
                AssemblyName an = new AssemblyName("socisaDll");
                Assembly a = Assembly.Load(an);
                Type[] ts = a.GetTypes();
                foreach (Type t in ts)
                {
                    try
                    {
                        if(t.FullName.IndexOf("SOCISA.Models") > -1)
                            FullyQualifiedNames.Add(t.Name, Assembly.CreateQualifiedName("socisaDll", t.FullName));
                    }
                    catch { }
                }
            }
            catch { }
            /* ****************************************************** */

            /* Get the predefined settings from AppSettings.json file */
            string settings = File.ReadAllText("AppSettings.json");
            dynamic result = JsonConvert.DeserializeObject(settings);
            string MySqlConnectionString = result.MySqlConnectionString;
            ThumbNailSizes[] tSizes = JsonConvert.DeserializeObject<ThumbNailSizes[]>(result.ThumbNailSizes.ToString());
            ScansFolder = result.ScansFolder;
            PdfsFolder = result.PdfsFolder;
            /* ****************************************************** */

            Console.Error.WriteLine(MySqlConnectionString);
            Console.Error.Write("\r\n=====================================================\r\n");
            //Console.Error.Write("Command: " + String.Join(" ", args));
            //Console.Error.Write("\r\n=====================================================\r\n");
            try
            {
                ParametersResponse pr = new ParametersResponse();
                if (args.Length > 0)
                {
                    if (args.Length == 1)
                        pr = GenerateParameters(args[0]);
                    else
                        pr = GenerateParameters(args);
                }
                if ((pr.DbDataBase != null && pr.DbDataBase != "") && (pr.DbUser != null && pr.DbUser != "") && (pr.DbPassword != null && pr.DbPassword != ""))
                    MySqlConnectionString = String.Format("Server={0};Port={1};Database={2};Uid={3};Pwd={4};", pr.DbHost, pr.DbPort, pr.DbDataBase, pr.DbUser, pr.DbPassword);

                IDatabase redis = null;
                try
                {
                    ConnectionMultiplexer redisCon = OpenRedisConnection("redis_server");
                    redis = redisCon.GetDatabase();

                    //IPAddress ip = IPAddress.Parse("172.18.0.3");
                    //redis = OpenRedisConnection(ip).GetDatabase();
                }
                catch(Exception exp) {
                    Console.Error.Write(exp.ToString());
                    Console.Error.Write("\r\n------------------------------------------------------\r\n");
                }
                //now start listening to Redis
                while (true)
                {
                    string Command = "";
                    if (redis != null)
                    {
                        Command = redis.ListRightPopAsync("Commands").Result;
                        //redis.ListLeftPopAsync("Commands").Result;
                    }
                    else
                    {
                        Command = String.Join(" ", args); // for local testing!
                    }

                    if (Command != null && Command != "")
                    {
                        Console.Error.Write("\r\nGot Redis command: " + Command);
                        pr = GenerateParameters(Command);
                        Console.Error.Write("\r\n\tRedisClientId: " + pr.RedisClientId + "\r\n\tPredicate: " + pr.CommandPredicate + "\r\n\tRepository: " + pr.CommandObjectRepository + "\r\n\tArguments: " + pr.CommandArguments + "\r\n\tMessageId: " + pr.MessageId);
                        Console.Error.Write("\r\n=====================================================\r\n");
                        if (pr.CommandPredicate != "" && pr.CommandObjectRepository != "")
                        {
                            try
                            {
                                if(pr.CommandPredicate != "Login" && (pr.AuthenticatedUserId == null || pr.AuthenticatedUserId == "")) // unauthorized user
                                {
                                    Console.Write("\r\nUtilizator neautentificat!\r\n");
                                    if (redis != null) {
                                        Error err = ErrorParser.ErrorMessage("unauthorisedUser");
                                        response rt = new response(false, err.ERROR_MESSAGE, null, null, new List<Error>() { err });
                                        NewResponse nrt = new NewResponse(rt);
                                        nrt.RedisClientId = pr.RedisClientId;
                                        nrt.MessageId = nrt.CorrelationId = pr.MessageId;
                                        redis.ListRightPushAsync(pr.RedisClientId, JsonConvert.SerializeObject(nrt));
                                        return;
                                    }
                                }
                                Type T = Type.GetType(FullyQualifiedNames[pr.CommandObjectRepository]);
                                var repositoryClass = Activator.CreateInstance(T, new object[] { Convert.ToInt32(pr.AuthenticatedUserId), MySqlConnectionString });
                                //MethodInfo mi = repositoryClass.GetType().GetMethod(CommandPredicate);
                                //get the wright method to run:
                                MethodInfo methodToRun = null;
                                try
                                {
                                    methodToRun = repositoryClass.GetType().GetMethod(pr.CommandPredicate);
                                    Console.Error.Write("Found Single Method: {0}.", methodToRun.Name);
                                    Console.Error.Write("\r\n=====================================================\r\n");
                                }
                                catch //there are more methods with the same name, but different parameters
                                {
                                    try
                                    {
                                        string[] sArgs = pr.CommandArguments.Split(CommandArgumentsSeparator.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                                        //string[] sArgs = GenerateArguments(CommandArguments);
                                        MethodInfo[] mis = repositoryClass.GetType().GetMethods();
                                        foreach (MethodInfo mi in mis)
                                        {
                                            if (mi.Name == pr.CommandPredicate && mi.GetParameters().Length == sArgs.Length)
                                            {
                                                //now we analyze the parameters types
                                                ParameterInfo[] pInfos = mi.GetParameters();
                                                int i = 0;
                                                while (i < pInfos.Length)
                                                {
                                                    T = pInfos[i].ParameterType;
                                                    try
                                                    {
                                                        /*
                                                        JsonSerializerSettings S = new JsonSerializerSettings();
                                                        S.StringEscapeHandling = StringEscapeHandling.Default;
                                                        S.ObjectCreationHandling = ObjectCreationHandling.Auto;
                                                        S.MissingMemberHandling = MissingMemberHandling.Error;
                                                        S.MetadataPropertyHandling = MetadataPropertyHandling.Default;
                                                        S.PreserveReferencesHandling = PreserveReferencesHandling.All;
                                                        S.TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full;                                                        
                                                        var tmpParameter = T.FullName.IndexOf("System.String") > -1 ? sArgs[i] : JsonConvert.DeserializeObject(sArgs[i], T, S);
                                                        */
                                                        var tmpParameter = T.FullName.IndexOf("System.String") > -1 ? sArgs[i] : JsonConvert.DeserializeObject(sArgs[i], T);
                                                        JObject jObj = null;
                                                        try
                                                        {
                                                            jObj = JObject.Parse(sArgs[i]);
                                                        }catch { }
                                                        //verificare suplimentara pt. cazul Update(json item), Update(string fields din item)
                                                        if (jObj != null && jObj.Count != tmpParameter.GetType().GetProperties().Length // este trimis doar un string cu fielduri, nu tot obiectul
                                                            && pr.CommandPredicate == "Update") // la insert e ok, dar la update poate sa trimita si ID-ul in json
                                                        {
                                                            throw new Exception("invalidCastException");
                                                        }
                                                    }
                                                    catch
                                                    {
                                                        break;
                                                    }
                                                    i++;
                                                }
                                                if (i == pInfos.Length) //all parameters are there
                                                {
                                                    Console.Error.Write("Found Multiple Methods: {0}.", mi.Name);
                                                    Console.Error.Write("\r\n=====================================================\r\n");
                                                    methodToRun = mi;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                ParameterInfo[] pis = methodToRun.GetParameters();
                                ArrayList lArgs = new ArrayList();
                                if (pr.CommandArguments != null && pr.CommandArguments.ToString() != "")
                                {
                                    string[] sArgs = pr.CommandArguments.Split(CommandArgumentsSeparator.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                                    //string[] sArgs = GenerateArguments(CommandArguments);
                                    if (pis.Length == sArgs.Length)
                                    {
                                        for (int i = 0; i < pis.Length; i++)
                                        {
                                            T = pis[i].ParameterType;

                                            var tmpParam = (T.FullName.IndexOf("System.String") > -1 && (sArgs[i] == null || sArgs[i].ToLower() == "null")) ? null : (T.FullName.IndexOf("System.String") > -1 ? sArgs[i] : JsonConvert.DeserializeObject(sArgs[i], T));
                                            try
                                            {
                                                PropertyInfo[] props = tmpParam.GetType().GetProperties();
                                                foreach (PropertyInfo prop in props)
                                                {
                                                    if (prop.Name == "authenticatedUserId")
                                                        prop.SetValue(tmpParam, Convert.ToInt32(pr.AuthenticatedUserId));
                                                    if (prop.Name == "connectionString")
                                                        prop.SetValue(tmpParam, MySqlConnectionString);
                                                }
                                                FieldInfo[] fis = tmpParam.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                                                foreach (FieldInfo fi in fis)
                                                {
                                                    try
                                                    {
                                                        if (fi.Name.IndexOf("authenticatedUserId") > -1)
                                                            fi.SetValue(tmpParam, Convert.ToInt32(pr.AuthenticatedUserId));
                                                        if (fi.Name.IndexOf("connectionString") > -1)
                                                            fi.SetValue(tmpParam, MySqlConnectionString);
                                                    }
                                                    catch (Exception exp) { Console.Error.Write(exp.ToString() + "\r\n"); }
                                                }
                                            }
                                            catch(Exception exp) { Console.Error.Write(exp.ToString() + "\r\n"); }
                                            lArgs.Add(tmpParam);
                                        }
                                    }
                                }

                                /*
                                var r = methodToRun.Invoke(repositoryClass, lArgs.Count > 0 ? lArgs.ToArray() : null);
                                try
                                {
                                    //((IDictionary<String, Object>)r).Remove("authenticatedUserId");
                                    //((IDictionary<String, Object>)r).Remove("connectionString");
                                    var ret = r.FixMeUp();
                                    Console.Error.Write(JsonConvert.SerializeObject(ret));
                                }
                                catch (Exception exp) { exp.ToString(); }
                                */
                                //dynamic r = methodToRun.Invoke(repositoryClass, lArgs.Count > 0 ? lArgs.ToArray() : null);
                                var r = methodToRun.Invoke(repositoryClass, lArgs.Count > 0 ? lArgs.ToArray() : null);
                                NewResponse nr = new NewResponse((response)r);
                                nr.RedisClientId = pr.RedisClientId;
                                nr.MessageId = nr.CorrelationId = pr.MessageId;

                                string toReturn = JsonConvert.SerializeObject(nr);
                                Console.Error.Write(toReturn);
                                //redis.ListRightPushAsync("Results", toReturn);
                                redis.ListRightPushAsync(pr.RedisClientId, toReturn);
                            }
                            catch (Exception exp)
                            {
                                Console.Error.Write(exp.ToString());
                                Console.Error.Write("\r\n=====================================================\r\n");
                            }
                        }
                    }
                    if (redis == null) break;
                    Thread.Sleep(1000);
                }
            }
            catch(Exception exp) {
                Console.Error.Write(exp.ToString());
                Console.Error.Write("\r\n=====================================================\r\n");
            }
            Console.Error.Write("Command run without errors.");
            Console.Error.Write("\r\n\r\n ***************************************************************\r\n\r\n");
        }

        private static string[] GenerateArguments(object ars) {
            if (ars is JObject) return GenerateArgs((JObject)ars);
            return GenerateArgs(Convert.ToString(ars));
        }

        private static string[] GenerateArgs(string ars)
        {
            return ars.Split(CommandArgumentsSeparator.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] GenerateArgs(JObject ars)
        {
            string[] toReturn = new string[ars.Count];
            int i = 0;
            foreach(var t in ars)
            {
                JToken j = t.Value;
                toReturn[i] = j.ToString() == "" || j == null ? null : j.ToString();
                i++;
            }
            return toReturn;
        }

        private static ParametersResponse GenerateParameters(string[] args)
        {
            ParametersResponse pr = new ParametersResponse();
            pr.AuthenticatedUserId = "1"; // to change after Authentication implementation !!!
            foreach (string arg in args)
            {
                if (arg.ToLower().IndexOf("host") > -1) pr.DbHost = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("port") > -1) pr.DbPort = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("user") > -1 && arg.ToLower().IndexOf("authenticated_user_id") < 0 && arg.ToLower().IndexOf("authenticated-user-id") < 0) pr.DbUser = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("database") > -1) pr.DbDataBase = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("password") > -1) pr.DbPassword = arg.Split('=')[1];

                if (arg.ToLower().IndexOf("command-predicate") > -1 || arg.ToLower().IndexOf("command_predicate") > -1) pr.CommandPredicate = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("command-object-repository") > -1 || arg.ToLower().IndexOf("command_object_repository") > -1) pr.CommandObjectRepository = arg.Split('=')[1];
                //if (arg.ToLower().IndexOf("command_arguments") > -1) CommandArguments = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("command-arguments") > -1 || arg.ToLower().IndexOf("command_arguments") > -1) pr.CommandArguments = arg.Replace("--command_arguments=", "").Replace("--command-arguments=", "");

                if (arg.ToLower().IndexOf("authenticated-user-id") > -1 || arg.ToLower().IndexOf("authenticated_user_id") > -1) pr.AuthenticatedUserId = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("redis-client-id") > -1 || arg.ToLower().IndexOf("redis_client_id") > -1) pr.RedisClientId = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("message-id") > -1 || arg.ToLower().IndexOf("message_id") > -1) pr.MessageId = arg.Split('=')[1];
            }
            return pr;
        }

        private static ParametersResponse GenerateParameters(string Command)
        {
            ParametersResponse pr = new ParametersResponse();
            pr.AuthenticatedUserId = "1"; // to change after Authentication implementation !!!
            try
            {
                dynamic jParams = JsonConvert.DeserializeObject(Command);
                try { if(jParams.host != null) pr.DbHost = jParams.host; } catch { }
                try { if (jParams.port != null) pr.DbPort = jParams.port; } catch { }
                try { if (jParams.database != null) pr.DbDataBase = jParams.database; } catch { }
                try { if (jParams.user != null) pr.DbUser = jParams.user; } catch { }
                try { if (jParams.password != null) pr.DbPassword = jParams.password; } catch { }
                try { if (jParams.authenticated_user_id != null) pr.AuthenticatedUserId = jParams.authenticated_user_id; } catch { }

                try { pr.RedisClientId = jParams.redis_client_id; } catch { }
                try { pr.MessageId = jParams.message_id; } catch { }
                try { pr.CommandPredicate = jParams.command_predicate; } catch { }
                try { pr.CommandObjectRepository = jParams.command_object_repository; } catch { }
                /*
                try {
                    pr.CommandArguments = null;
                    try
                    {
                        JObject jObj = JObject.Parse(jParams.command_arguments.ToString());
                        pr.CommandArguments = jObj;
                    }
                    catch(Exception exp) { pr.CommandArguments = jParams.command_arguments; }
                } catch(Exception exp) { LogWriter.Log(exp); }
                */
                try {
                    string x = jParams.command_arguments.GetType().FullName;
                    bool tmp = x.IndexOf("System.String") > -1 || x.IndexOf("Linq.JValue") > -1;
                    pr.CommandArguments = tmp ? jParams.command_arguments : JsonConvert.SerializeObject(jParams.command_arguments); } catch { }
                return pr;
            }
            catch
            {
                string[] args = Command.Split(' ');
                return GenerateParameters(args);
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntryAsync(hostname)
                .Result
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static string GetIp2(string hostname)
        {
            IPHostEntry ihe = Dns.GetHostEntryAsync(hostname).Result;
            IPAddress[] ias = ihe.AddressList;
            foreach (IPAddress ia in ias)
                if (ia.AddressFamily == AddressFamily.InterNetwork)
                    return ia.ToString();
            return null;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            // Use IP address to workaround hhttps://github.com/StackExchange/StackExchange.Redis/issues/410
            var ipAddress = GetIp2(hostname);
            Console.WriteLine($"Found redis at {ipAddress}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connected to redis");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static ConnectionMultiplexer OpenRedisConnection(IPAddress ip)
        {
            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connected to redis");
                    return ConnectionMultiplexer.Connect(ip.ToString());
                }
                catch (RedisConnectionException rexp)
                {
                    Console.Error.Write(rexp.ToString() + "\r\n");
                    Console.Error.WriteLine("Waiting for redis\r\n");
                    Thread.Sleep(1000);
                }
            }
        }

        #region Test
        /*
        private static void Test(string[] args)
        {
            string S = File.ReadAllText("AppSettings.json");
            dynamic R = JsonConvert.DeserializeObject(S);
            ConnectionString = R.MySqlConnectionString;
            ThumbNailSizes[] tSizes = JsonConvert.DeserializeObject<ThumbNailSizes[]>(R.ThumbNailSizes.ToString());

            foreach (string arg in args)
            {
                if (arg.ToLower().IndexOf("host") > -1) DbHost = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("port") > -1) DbPort = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("user") > -1 && arg.ToLower().IndexOf("authenticated_user_id") < 0) DbUser = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("database") > -1) DbDataBase = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("password") > -1) DbPassword = arg.Split('=')[1];

                if (arg.ToLower().IndexOf("file-path") > -1) CommandPredicate = arg.Split('=')[1];
                if (arg.ToLower().IndexOf("id") > -1) CommandObjectRepository = arg.Split('=')[1];
            }

            var redis = OpenRedisConnection("redis").GetDatabase();
            var arguments = redis.ListLeftPopAsync("commands").Result;

            //byte[] bs = CommonFunctions.GetTemplateFileFromDb(Convert.ToInt32(AuthenticatedUserId), M, 1);
            //Dosar d = Find(Convert.ToInt32(CommandObjectRepository));
            //Console.Error.Write( ExportDosarToPdf(bs, d));
            

            DocumentScanat ds = new DocumentScanat(Convert.ToInt32(AuthenticatedUserId), ConnectionString);
            FileInfo fi = new FileInfo("test1.jpg");
            FileStream fs = File.OpenRead(fi.Name);
            byte[] tjpg = new byte[fs.Length];
            fs.Read(tjpg, 0, (int)fs.Length);
            ds.DATA_INCARCARE = DateTime.Now;
            ds.DENUMIRE_FISIER = fi.Name;
            ds.EXTENSIE_FISIER = fi.Extension;
            ds.DIMENSIUNE_FISIER = fs.Length;
            ds.DETALII = "Test";
            ds.ID_DOSAR = 1;
            ds.ID_TIP_DOCUMENT = 1;
            ds.FILE_CONTENT = tjpg;
            ds.Insert(tSizes);
            

            
            DocumentScanat ds = new DocumentScanat(Convert.ToInt32(AuthenticatedUserId), ConnectionString, 22);
            FileStream fs = File.OpenWrite("tmp1.gif");
            fs.Write(ds.SMALL_ICON, 0, ds.SMALL_ICON.Length);
            fs.Flush();
            fs.Dispose();
            fs = File.OpenWrite("tmp2.gif");
            fs.Write(ds.MEDIUM_ICON, 0, ds.MEDIUM_ICON.Length);
            fs.Flush();
            fs.Dispose();
            using (MagickImageCollection images = new MagickImageCollection())
            {
                //images.Read(_documentScanat.DENUMIRE_FISIER, settings);
                images.Read(ds.FILE_CONTENT);
                MagickImage image = images[0];
                image.Resize(100, 130);
                image.Format = MagickFormat.Gif;
                image.BackgroundColor = MagickColors.White;
                image.BorderColor = MagickColors.Red;
                MemoryStream ms = new MemoryStream();                
                ds.SMALL_ICON = image.ToByteArray();
                ds.Update();
            }
            return;
            // END TEST !!!!!
        }

        public static bool LoadDocFileIntoDb(int _authenticatedUserId, string _connectionString, string filePath, string _DETALII)
        {
            try
            {
                FileInfo fi = new FileInfo(filePath);
                int FileSize;
                byte[] rawData;
                FileStream fs;
                fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                FileSize = (int)fs.Length;

                rawData = new byte[FileSize];
                fs.Read(rawData, 0, FileSize);
                DataAccess da = new DataAccess(_authenticatedUserId, _connectionString, System.Data.CommandType.StoredProcedure, "DOCUMENTE_SCANATEsp_insert", new object[]
                {
                    new MySqlParameter("_DENUMIRE_FISIER", fi.Name),
                    new MySqlParameter("_EXTENSIE_FISIER", fi.Extension),
                    new MySqlParameter("_DIMENSIUNE_FISIER", FileSize),
                    new MySqlParameter("_FILE_CONTENT", rawData),
                    new MySqlParameter("_DETALII", _DETALII)
                });
                response r = da.ExecuteInsertQuery();
                return r.Status;
            }
            catch { return false; }
        }

        public static string GetDocFileFromDb(int _authenticatedUserId, string _connectionString, string fileName)
        {
            try
            {
                int FileSize;
                byte[] rawData;
                FileStream fs;
                DataAccess da = new DataAccess(_authenticatedUserId, _connectionString, System.Data.CommandType.StoredProcedure, "TEMPLATESsp_GetByName", new object[]
                {
                    new MySqlParameter("_DENUMIRE_FISIER", fileName)
                });
                IDataReader r = da.ExecuteSelectQuery();
                while (r.Read())
                {
                    FileSize = Convert.ToInt32(r["DIMENSIUNE_FISIER"]);
                    rawData = (byte[])r["FILE_CONTENT"];

                    fs = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write);
                    fs.Write(rawData, 0, (int)FileSize);
                    break;
                }
                return fileName;
            }
            catch { return null; }
        }

        public static string ExportDosarToPdf(string templateFileName, Dosar dosar)
        {
            try
            {
                FileStream fs = new FileStream(templateFileName, FileMode.Open, FileAccess.Read);
                byte[] bs = new byte[fs.Length];
                int n = fs.Read(bs, 0, (int)fs.Length);
                return ExportDosarToPdf(bs, dosar);
            }
            catch
            {
                return null;
            }
        }

        public static string ExportDosarToPdf(byte[] template_file_content, Dosar dosar)
        {
            string fileName = dosar.NR_DOSAR_CASCO + "_cerere.pdf";
            MemoryStream ms = new MemoryStream(template_file_content);
            FileStream fs1 = File.Open(fileName, FileMode.Create, FileAccess.ReadWrite);
            PdfFixedDocument poDocument = new PdfFixedDocument(ms);

            SocietateAsigurare sCasco = dosar.GetSocietateCasco();
            SocietateAsigurare sRca = dosar.GetSocietateRca();
            Asigurat aCasco = dosar.GetAsiguratCasco();
            Asigurat aRca = dosar.GetAsiguratRca();
            Auto autoCasco = dosar.GetAutoCasco();
            Auto autoRca = dosar.GetAutoRca();

            poDocument.Form.Fields["FieldSocietateCasco"].Value = sCasco.DENUMIRE;
            poDocument.Form.Fields["FieldAdresaSocietateCasco"].Value = sCasco.ADRESA;
            poDocument.Form.Fields["FieldCUISocietateCasco"].Value = sCasco.CUI;
            poDocument.Form.Fields["FieldContBancarSocietateCasco"].Value = sCasco.IBAN;
            poDocument.Form.Fields["FieldBancaSocietateCasco"].Value = sCasco.BANCA;
            poDocument.Form.Fields["FieldSocietateRCA"].Value = sRca.DENUMIRE;
            poDocument.Form.Fields["FieldAdresaSocietateRCA"].Value = sRca.ADRESA;
            poDocument.Form.Fields["FieldNrDosarCasco"].Value = dosar.NR_DOSAR_CASCO;
            poDocument.Form.Fields["FieldPolitaRCA"].Value = dosar.NR_POLITA_RCA;
            poDocument.Form.Fields["FieldPolitaCasco"].Value = dosar.NR_POLITA_CASCO;
            poDocument.Form.Fields["FieldAsiguratCasco"].Value = aCasco.DENUMIRE;
            poDocument.Form.Fields["FieldAsiguratRCA"].Value = aRca.DENUMIRE;
            poDocument.Form.Fields["FieldNrAutoCasco"].Value = autoCasco.NR_AUTO;
            poDocument.Form.Fields["FieldAutoCasco"].Value = autoCasco.MARCA + " " + autoCasco.MODEL;
            poDocument.Form.Fields["FieldNrAutoRCA"].Value = autoRca.NR_AUTO;
            poDocument.Form.Fields["FieldDataEveniment"].Value = Convert.ToDateTime(dosar.DATA_EVENIMENT).ToString("dd/MM/yyyy");
            poDocument.Form.Fields["FieldSuma"].Value = dosar.VALOARE_DAUNA.ToString();

            string docs = "";
            DocumentScanat[] dsj = dosar.GetDocumente();
            foreach (DocumentScanat doc in dsj)
            {
                docs = String.Format("- {1}\r\n{0}", docs, (doc.DETALII != "" && doc.DETALII != null ? doc.DETALII : doc.DENUMIRE_FISIER));
            }
            poDocument.Form.Fields["FieldDocumente"].Value = docs;

            poDocument.Form.FlattenFields();

            poDocument.Save(fs1);
            fs1.Flush();
            fs1.Dispose();
            return fileName;
        }

        public static string ExportDocumenteDosarToPdf(Dosar dosar)
        {
            try
            {
                PdfFixedDocument poDocument = new PdfFixedDocument();
                foreach (DocumentScanat dsj in dosar.GetDocumente())
                {
                    MemoryStream ms = new MemoryStream(dsj.FILE_CONTENT);
                    PdfFixedDocument pd = new PdfFixedDocument(ms);

                    switch (dsj.EXTENSIE_FISIER.Replace(".", "").ToLower())
                    {
                        case "pdf":
                            for (int i = 0; i < pd.Pages.Count; i++)
                                poDocument.Pages.Add(pd.Pages[i]);
                            break;
                        case "png":
                            Xfinium.Pdf.Graphics.PdfPngImage pngImg = new Xfinium.Pdf.Graphics.PdfPngImage(ms);
                            PdfPage p = new PdfPage();
                            p.Graphics.DrawImage(pngImg, 0, 0, p.Width, p.Height);
                            poDocument.Pages.Add(p);
                            break;
                        case "jpg":
                            Xfinium.Pdf.Graphics.PdfJpegImage jpgImg = new Xfinium.Pdf.Graphics.PdfJpegImage(ms);
                            p = new PdfPage();
                            p.Graphics.DrawImage(jpgImg, 0, 0, p.Width, p.Height);
                            poDocument.Pages.Add(p);
                            break;
                    }
                }
                string fileName = dosar.NR_DOSAR_CASCO + "_documente.pdf";
                FileStream fsNew = new FileStream(fileName, FileMode.CreateNew);
                poDocument.Save(fsNew);
                return fileName;
            }
            catch { return null; }
        }

        public static Dosar Find(int _id)
        {
            return new DosareRepository(Convert.ToInt32(AuthenticatedUserId), ConnectionString).Find(_id);
        }

        public static string ExportDosarCompletToPdf(string templateFileName, Dosar dosar)
        {
            try
            {
                string f1 = ExportDosarToPdf(templateFileName, dosar);
                string f2 = ExportDocumenteDosarToPdf(dosar);

                FileStream fs1 = new FileStream(f1, FileMode.Open);
                PdfFixedDocument p1 = new PdfFixedDocument(fs1);
                FileStream fs2 = new FileStream(f2, FileMode.Open);
                PdfFixedDocument p2 = new PdfFixedDocument(fs2);
                for (int i = 0; i < p2.Pages.Count; i++)
                {
                    p1.Pages.Add(p2.Pages[i]);
                }
                string fileNameToReturn = dosar.NR_DOSAR_CASCO + ".pdf";
                FileStream fs = new FileStream(fileNameToReturn, FileMode.CreateNew);
                p1.Save(fs);
                return fileNameToReturn;
            }
            catch { return null; }
        }
        */
        #endregion
    }
}
