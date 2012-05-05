// Copyright (c) <2012> <Playdead>
// This file is subject to the MIT License as seen in the trunk of this repository
// Maintained by: <Kristian Kjems> <kristian.kjems+UnitySVN@gmail.com>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using CommandLineExecution;

namespace VersionControl.Backend.SVN
{
    public class SVNCommands : IVersionControlCommands
    {
        private string workingDirectory = ".";
        private string userName;
        private string password;
        private string versionNumber;
        private StatusDatabase statusDatabase = new StatusDatabase();
        private bool operationActive = false;
        private readonly object operationActiveLockToken = new object();

        public bool IsReady()
        {
            return !operationActive;
        }

        public void SetWorkingDirectory(string workingDirectory)
        {
            this.workingDirectory = workingDirectory;
        }

        public void SetUserCredentials(string userName, string password)
        {
            if (!string.IsNullOrEmpty(userName)) this.userName = userName;
            if (!string.IsNullOrEmpty(password)) this.password = password;
        }

        public VersionControlStatus GetAssetStatus(string assetPath)
        {
            assetPath = assetPath.Replace("\\", "/");
            return statusDatabase[assetPath];
        }

        public IEnumerable<string> GetFilteredAssets(Func<string, VersionControlStatus, bool> filter)
        {
            return new List<string>(statusDatabase.Keys).Where(k => filter(k, statusDatabase[k])).ToList();
        }

        public bool Status(bool remote, bool full)
        {
            string arguments = "status --xml";
            if (remote) arguments += " -u";
            if (full) arguments += " -v";

            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteCommandLine(svnStatusTask);
            }

            if (commandLineOutput.Failed) return false;
            try
            {
                statusDatabase = SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr);
            }
            catch (XmlException)
            {
                return false;
            }
            return true;
        }

        public bool Status(IEnumerable<string> assets, bool remote)
        {
            string arguments = "status --xml -v --depth=empty ";
            if (remote) arguments += "-u ";
            arguments += ConcatAssetPaths(assets);
            foreach (var assetIt in assets)
            {
                statusDatabase[assetIt] = new VersionControlStatus { assetPath = assetIt, reflectionLevel = VCReflectionLevel.Pending };
            }
            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteCommandLine(svnStatusTask);
            }

            if (commandLineOutput.Failed) return false;
            try
            {
                foreach (var statusIt in SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr))
                {
                    statusDatabase[statusIt.Key] = statusIt.Value;
                }
            }
            catch (XmlException)
            {
                return false;
            }
            return true;
        }

        private CommandLine CreateSVNCommandLine(string arguments)
        {
            arguments = "--non-interactive " + arguments;
            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password))
            {
                arguments = " --username " + userName + " --password " + password + " --no-auth-cache " + arguments;
            }
            return new CommandLine("svn", arguments, workingDirectory);
        }

        private bool CreateOperation(string arguments)
        {
            CommandLineOutput commandLineOutput;
            using (var commandLineOperation = CreateSVNCommandLine(arguments))
            {
                commandLineOperation.OutputReceived += OnProgressInformation;
                commandLineOperation.ErrorReceived += OnProgressInformation;
                commandLineOutput = ExecuteCommandLine(commandLineOperation);
            }
            return !commandLineOutput.Failed;
        }

        private CommandLineOutput ExecuteCommandLine(CommandLine commandLine)
        {
            CommandLineOutput commandLineOutput;
            lock (operationActiveLockToken)
            {
                //D.Log(">>>> Locked >>>>");
                try
                {
                    operationActive = true;
                    D.Log(commandLine.ToString());
                    //System.Threading.Thread.Sleep(100); // emulate latency to SVN server
                    commandLineOutput = commandLine.Execute();
                }
                catch (Exception e)
                {
                    throw new VCCriticalException("Check that your commandline SVN client is installed corretly\n\n" + e.Message, commandLine.ToString(), e);
                }
                finally
                {
                    operationActive = false;
                    //D.Log("<<<< UnLocked <<<<");
                }
            }
            if (commandLineOutput.Arguments.Contains("ExceptionTest.txt"))
            {
                throw new VCException("Test Exception cast due to ExceptionTest.txt being a part of arguments", commandLine.ToString());
            }
            if (!string.IsNullOrEmpty(commandLineOutput.ErrorStr))
            {
                var errStr = commandLineOutput.ErrorStr;
                if (errStr.Contains("E730060") || errStr.Contains("Unable to connect") || errStr.Contains("is unreachable") || errStr.Contains("Operation timed out") || errStr.Contains("Can't connect to"))
                    throw new VCConnectionTimeoutException(errStr, commandLine.ToString());
                if (errStr.Contains("W160042") || errStr.Contains("Newer Version"))
                    throw new VCNewerVersionException(errStr, commandLine.ToString());
                if (errStr.Contains("W155007") || errStr.Contains("'" + workingDirectory + "'" + " is not a working copy"))
                    throw new VCCriticalException(errStr, commandLine.ToString());
                if (errStr.Contains("E160028") || errStr.Contains("is out of date"))
                    throw new VCOutOfDate(errStr, commandLine.ToString());
                if (errStr.Contains("E155037") || errStr.Contains("E155004") || errStr.Contains("run 'svn cleanup'"))
                    throw new VCLocalCopyLockedException(errStr, commandLine.ToString());
                throw new VCException(errStr, commandLine.ToString());
            }
            return commandLineOutput;
        }

        private bool CreateAssetOperation(string arguments, IEnumerable<string> assets)
        {
            if (assets == null || !assets.Any()) return true;
            return CreateOperation(arguments + ConcatAssetPaths(assets));
        }

        private static string FixAtChar(string asset)
        {
            return asset.Contains("@") ? asset + "@" : asset;
        }

        private static string ConcatAssetPaths(IEnumerable<string> assets)
        {
            assets = assets.Select(a => a.Replace("\\", "/"));
            assets = assets.Select(FixAtChar);
            if (assets.Any()) return " \"" + assets.Aggregate((i, j) => i + "\" \"" + j) + "\"";
            return "";
        }

        public bool Update(IEnumerable<string> assets = null, bool force = true)
        {
            if (assets == null || !assets.Any()) assets = new[] { workingDirectory };
            return CreateAssetOperation("update" + (force ? " --force" : ""), assets);
        }

        public bool Commit(IEnumerable<string> assets, string commitMessage = "")
        {
            return CreateAssetOperation("commit -m \"" + commitMessage + "\"", assets);
        }

        public bool Add(IEnumerable<string> assets)
        {
            return CreateAssetOperation("add", assets);
        }

        public bool Revert(IEnumerable<string> assets)
        {
            return CreateAssetOperation("revert --depth=infinity", assets);
        }

        public bool Delete(IEnumerable<string> assets, bool force = false)
        {
            return CreateAssetOperation("delete" + (force ? " --force" : ""), assets);
        }

        public bool GetLock(IEnumerable<string> assets, bool force)
        {
            return CreateAssetOperation("lock" + (force ? " --force" : ""), assets);
        }

        public bool ReleaseLock(IEnumerable<string> assets)
        {
            return CreateAssetOperation("unlock", assets);
        }

        public bool ChangeListAdd(IEnumerable<string> assets, string changelist)
        {
            return CreateAssetOperation("changelist " + changelist, assets);
        }

        public bool ChangeListRemove(IEnumerable<string> assets)
        {
            return CreateAssetOperation("changelist --remove", assets);
        }

        public bool Checkout(string url, string path = "")
        {
            return CreateOperation("checkout \"" + url + "\" \"" + (path == "" ? workingDirectory : path) + "\"");
        }

        public bool Resolve(IEnumerable<string> assets, ConflictResolution conflictResolution)
        {
            if (conflictResolution == ConflictResolution.Ignore) return true;
            string conflictparameter = conflictResolution == ConflictResolution.Theirs ? "--accept theirs-full" : "--accept mine-full";
            return CreateAssetOperation("resolve " + conflictparameter, assets);
        }

        public bool Move(string from, string to)
        {
            return CreateOperation("move \"" + from + "\" \"" + to + "\"");
        }

        public string GetBasePath(string assetPath)
        {
            if (string.IsNullOrEmpty(versionNumber))
            {
                versionNumber = CreateSVNCommandLine("--version --quiet").Execute().OutputStr;
            }
            if (versionNumber.StartsWith("1.7"))
            {
                var svnInfo = CreateSVNCommandLine("info --xml " + assetPath).Execute();
                if (!svnInfo.Failed)
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(svnInfo.OutputStr);
                    var checksumNode = xmlDoc.GetElementsByTagName("checksum").Item(0);
                    var rootPathNode = xmlDoc.GetElementsByTagName("wcroot-abspath").Item(0);

                    if (checksumNode != null && rootPathNode != null)
                    {
                        string checksum = checksumNode.InnerText;
                        string firstTwo = checksum.Substring(0, 2);
                        string rootPath = rootPathNode.InnerText;
                        string basePath = rootPath + "/.svn/pristine/" + firstTwo + "/" + checksum + ".svn-base";
                        if (File.Exists(basePath)) return basePath;
                    }
                }
            }
            if (versionNumber.StartsWith("1.6"))
            {
                return Path.GetDirectoryName(assetPath) + "/.svn/text-base/" + Path.GetFileName(assetPath) + ".svn-base";
            }
            return "";
        }

        public bool CleanUp()
        {
            return CreateOperation("cleanup");
        }

        public void ClearDatabase()
        {
            statusDatabase.Clear();
        }

        public event Action<string> ProgressInformation;

        private void OnProgressInformation(string info)
        {
            if (ProgressInformation != null) ProgressInformation(info);
        }
    }
}