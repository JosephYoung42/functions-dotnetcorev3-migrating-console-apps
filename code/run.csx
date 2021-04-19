#r "Newtonsoft.Json" 
using System.Net;
using Newtonsoft.Json;
using System.IO;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;

//Code from https://github.com/Azure-Samples/functions-dotnet-migrating-console-apps/edit/master/code/run.csx
//Written by Ambrose http://github.com/efficientHacks and Murali http://github.com/muralivp
//.Net Core v3 Modifications by Joseph Young https://github.com/JosephYoung42

public class ExeArg
{
    public string Name { get; set; }
    public string Type { get; set; }
    public string Value { get; set; }
}

public static async Task<HttpResponseMessage> Run(HttpRequest req, ILogger log)
{
    log.LogInformation("C# HTTP trigger function processed a request.");

    string localPath = req.Path;
    string functionName = localPath.Substring(localPath.LastIndexOf('/')+1);

    var json = File.ReadAllText(string.Format(@"C:\home\site\wwwroot\{0}\FunctionConfig.json",functionName));

    var config = JsonConvert.DeserializeObject<dynamic>(json);

    var functionArguments = config.input.arguments;
    var localOutputFolder = Path.Combine(@"C:\home\data", config.output.folder.Value, Path.GetFileNameWithoutExtension(Path.GetTempFileName()));
    Directory.CreateDirectory(localOutputFolder);
    var workingDirectory = Path.Combine(@"C:\home\site\wwwroot", functionName + "\\bin");
    Directory.SetCurrentDirectory(workingDirectory);//fun fact - the default working directory is d:\windows\system32

    var command = config.input.command.Value;

    var argList = new List<ExeArg>();

    //Get the body from the request onlly once to avoid disposal of Stream object
    string requestBody = await getBodyFromRequest(req);

    //Parse the config file's arguments
    //handle file system, local file etc. and construct the input params for the actual calling of the .exe
    foreach (var arg in functionArguments)
    {
        var exeArg = new ExeArg();
        exeArg.Name = arg.Name;
        var value = (Newtonsoft.Json.Linq.JObject)arg.Value;
        var property = (Newtonsoft.Json.Linq.JProperty)value.First;
        exeArg.Type = property.Name;
        exeArg.Value = property.Value.ToString();

        var valueFromQueryString = getValueFromQuery(req, requestBody, exeArg.Name, log);

        log.LogInformation($"valueFromQueryString name={exeArg.Name}");
        log.LogInformation($"valueFromQueryString val={valueFromQueryString}");
        if(!string.IsNullOrEmpty(valueFromQueryString))
        {
            exeArg.Value = valueFromQueryString;
            log.LogInformation(exeArg.Name + " " + valueFromQueryString);
        }

        if(exeArg.Type.ToLower() == "localfile" || exeArg.Type.ToLower() == "localfolder")
        {
            exeArg.Value = Path.Combine(localOutputFolder, exeArg.Value);
            exeArg.Type = "string";
        }
        if(string.IsNullOrEmpty(exeArg.Value))
        {
            //throw exception here
        }
        argList.Add(exeArg);
    }

    //call the exe
    Dictionary<string, string> paramList = ProcessParameters(argList, localOutputFolder);
    foreach (string parameter in paramList.Keys)
    {
        command = command.Replace(parameter, paramList[parameter]);
    }
    string commandName = command.Split(' ')[0];
    string arguments = command.Split(new char[] { ' ' }, 2)[1];
    log.LogInformation($"the command is {command}");
    log.LogInformation($"the working dir is {workingDirectory}");
    Process p = new Process();
    p.StartInfo.UseShellExecute = false;
    p.StartInfo.RedirectStandardOutput = true;
    p.StartInfo.FileName = commandName; 
    p.StartInfo.Arguments = arguments;
    p.Start();
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    File.WriteAllText(localOutputFolder+"\\out.txt",output);

    //handle return file
    log.LogInformation($"handling return file localOutputFolder={localOutputFolder}");
    string outputFile = config.output.binaryFile.returnFile.Value;
    string outputFileName = config.output.binaryFile.returnFileName.Value;
    var path = Directory.GetFiles(localOutputFolder, outputFile)[0];
    
    log.LogInformation($"returning this file {path}");
    var result = new FileHttpResponseMessage(localOutputFolder);
    var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
    result.Content = new StreamContent(stream);
    result.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    result.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = outputFileName
    };

    return result;
}

private static Dictionary<string, string> ProcessParameters(List<ExeArg> arguments, string outputFolder)
{
    Dictionary<string, string> paramList = new Dictionary<string, string>();
    foreach (var arg in arguments)
    {
        switch (arg.Type)
        {
            case "url":
                string downloadedFileName = ProcessUrlType((string)arg.Value, outputFolder);
                paramList.Add("{" + arg.Name + "}", downloadedFileName);
                break;
            case "string":
                paramList.Add("{" + arg.Name + "}", arg.Value.ToString());
                break;
            default:
                break;
        }
    }
    return paramList;
}

//you can modify this method to handle different URLs if necessary
private static string ProcessUrlType(string url, string outputFolder)
{
    Directory.CreateDirectory(outputFolder);
    string downloadedFile = Path.Combine(outputFolder, Path.GetFileName(Path.GetTempFileName()));
    //for oneDrive links 
    // (Doesn't work for current OneDrive Links due to 301 redirect error)
    // HttpWebRequest webRequest = (HttpWebRequest)HttpWebRequest.Create(url);
    // webRequest.AllowAutoRedirect = false;
    // WebResponse webResp = webRequest.GetResponse();
    // webRequest = (HttpWebRequest)HttpWebRequest.Create(webResp.Headers["Location"].Replace("redir", "download"));
    // webResp = webRequest.GetResponse();
    // string fileUrl = webResp.Headers["Content-Location"];

    WebClient webClient = new WebClient();
    webClient.DownloadFile(url, downloadedFile);
    return downloadedFile;
}

private async static Task<string> getBodyFromRequest(HttpRequest req)
{    
    // Get request body
    string requestBody = String.Empty;
    using StreamReader streamReader =  new StreamReader(req.Body);
    requestBody = await streamReader.ReadToEndAsync();
    return requestBody;
}

private static string getValueFromQuery(HttpRequest req, string requestBody, string name, ILogger log)
{
    // parse query parameter
    string value = req.Query[$"{name}"];

    //if not found in query string, look for it in the body (json)    
    dynamic data = JsonConvert.DeserializeObject(requestBody);

    // Set name to query string or body data
    value = value ?? data?[name];
    return value;
}

//this is to delete the folder after the response
//thanks to: https://stackoverflow.com/a/30522890/2205372
public class FileHttpResponseMessage : HttpResponseMessage
{
    private string filePath;

    public FileHttpResponseMessage(string filePath)
    {
        this.filePath = filePath;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Content.Dispose();
        Directory.Delete(filePath,true);
    }
}
