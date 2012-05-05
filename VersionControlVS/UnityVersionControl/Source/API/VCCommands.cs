// Copyright (c) <2012> <Playdead>
// This file is subject to the MIT License as seen in the trunk of this repository
// Maintained by: <Kristian Kjems> <kristian.kjems+UnitySVN@gmail.com>
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace VersionControl
{
    using UnityEngine;
    using UnityEditor;
    using UserInterface;

    [InitializeOnLoad]
    public class VCCommandsOnLoad
    {
        static VCCommandsOnLoad() { OnNextUpdate.Do(VCCommands.Initialize); }
    }

    public static class VersionControlStatusExtension
    {
        public static VersionControlStatus MetaStatus(this VersionControlStatus vcs)
        {
            return VCCommands.Instance.GetAssetStatus(vcs.assetPath + ".meta");
        }
    }

    /// <summary>
    /// Wraps the underlying IVersionControlCommands and add handling of .meta files and other Unity specific concerns.
    /// OnNextUpdate.Do is used to delay callback actions to the next Unity update as Unity does not allow calls into UnityEngine
    /// or UnityEditor if not orriginating from Unity's update loop.
    /// </summary>
    public sealed class VCCommands : IVersionControlCommands
    {
        private VCCommands()
        {
            vcc.SetWorkingDirectory(Application.dataPath.Remove(Application.dataPath.LastIndexOf("/Assets", StringComparison.Ordinal)));
            RequestStatus();
            EditorApplication.playmodeStateChanged += () => RequestStatus();
            vcc.ProgressInformation += progress => { if (ProgressInformation != null) OnNextUpdate.Do(() => ProgressInformation(progress)); };
            EditorApplication.update += Update;
        }

        static VCCommands instance;
        public static void Initialize() { if (instance == null) { instance = new VCCommands(); } }
        public static VCCommands Instance { get { Initialize(); return instance; } }

        private readonly IVersionControlCommands vcc = VersionControlFactory.CreateVersionControlCommands();
        private int statusRequests;
        private bool statusPending;

        public static bool Active
        {
            get
            {
                bool playmode = ThreadUtility.IsMainThread() && EditorApplication.isPlayingOrWillChangePlaymode;
                return VCSettings.VCEnabled && !playmode;
            }
        }

        public bool Ready { get { return Active && vcc.IsReady() && statusRequests == 0; } }

        public bool RequestStatus()
        {
            statusRequests++;
            //D.Log("RequestStatus : " + statusRequests);
            return true;
        }

        private void Update()
        {
            if (!statusPending && statusRequests > 0 && vcc.IsReady() && !EditorApplication.isCompiling)
            {
                statusPending = true;
                StatusTask().ContinueWithOnNextUpdate(t => { statusRequests = 0; statusPending = false; });
            }
        }

        #region Private methods

        private bool HandleExceptions(Func<bool> func)
        {
            if (Active)
            {
                try
                {
                    var result = func();
                    return result;
                }
                catch (VCException vcException)
                {
                    VCExceptionHandler.HandleException(vcException);
                }
                catch (Exception e)
                {
                    Debug.LogError("Unhandled exception: " + e.Message + "\n" + D.GetCallstack());
                    throw;
                }
            }
            else
            {
                D.Log("VC Action ignored due to not being active");
            }
            return false;
        }

        private void FlushFiles()
        {
            if (ThreadUtility.IsMainThread())
            {
                EditorApplication.SaveAssets();
                EditorUtility.UnloadUnusedAssets();
            }
            //else Debug.Log("Ignoring 'FlushFiles' due to Execution context");
        }


        private IEnumerable<string> AddMeta(IEnumerable<string> assets, bool includeNormal = false)
        {
            if (!assets.Any()) return assets;
            var metaFiles = new List<string>();
            foreach (var assetPathIt in assets)
            {
                if (!assetPathIt.EndsWith(".meta"))
                {
                    var metaAssetPath = assetPathIt + ".meta";
                    var metaStatus = GetAssetStatus(assetPathIt).MetaStatus();
                    if (includeNormal || metaStatus.fileStatus != VCFileStatus.Normal)
                    {
                        metaFiles.Add(metaAssetPath);
                    }
                }
            }
            return assets.Concat(metaFiles).Distinct().OrderByDescending(a => a.EndsWith(".meta"));
        }

        private IEnumerable<string> RemoveMetaPostFix(IEnumerable<string> assets)
        {
            return assets.Select(a => a.EndsWith(".meta") ? a.Remove(a.Length - 5) : a).Distinct();
        }

        private IEnumerable<string> AddFolders(IEnumerable<string> assets)
        {
            return assets
                .Select(a => Path.GetDirectoryName(a))
                .Where(d => GetAssetStatus(d).fileStatus != VCFileStatus.Normal)
                .Concat(assets)
                .Distinct();
        }

        private IEnumerable<string> AddFilesInFolders(IEnumerable<string> assets)
        {
            foreach (var assetIt in new List<string>(assets))
            {
                if (Directory.Exists(assetIt))
                {
                    assets = assets
                        .Concat(Directory.GetFiles(assetIt, "*", SearchOption.AllDirectories)
                        .Where(a => File.Exists(a) && !a.Contains(".meta") && !a.Contains("/.") && !a.Contains("\\.") && (File.GetAttributes(a) & FileAttributes.Hidden) == 0)
                        .Select(s => s.Replace("\\", "/")));
                }
            }
            return assets;
        }

        private static IEnumerable<string> AddDeletedInFolders(IEnumerable<string> assetPaths)
        {
            var deletedInFolders = assetPaths
                .Where(Directory.Exists)
                .SelectMany(d => VCCommands.Instance.GetFilteredAssets((assetPath, status) =>
                    (status.fileStatus == VCFileStatus.Deleted || status.fileStatus == VCFileStatus.Missing) && assetPath.StartsWith(d)));
            return assetPaths.Concat(deletedInFolders);
        }


        private static bool OpenCommitDialogWindow(IEnumerable<string> assets, IEnumerable<string> dependencies)
        {
            var commitWindow = ScriptableObject.CreateInstance<VCCommitWindow>();
            commitWindow.minSize = new Vector2(220, 140);
            commitWindow.title = "Commit...";
            commitWindow.SetAssetPaths(assets, dependencies);
            commitWindow.ShowUtility();
            return commitWindow.commitedFiles.Any();
        }

        private static IEnumerable<string> GetDependencies(IEnumerable<string> assetPaths)
        {
            return AssetDatabase.GetDependencies(assetPaths.ToArray())
                .Where(dep => Instance.GetAssetStatus(dep).fileStatus != VCFileStatus.Normal)
                .Except(assetPaths.Select(ap => ap.ToLowerInvariant()));
        }
        #endregion

        #region IVersionControlCommands Tasks

        private Task<bool> StartTask(Func<bool> work)
        {
            var task = (Active && vcc.IsReady()) ? new Task<bool>(work) : new Task<bool>(() => false);
            task.Start(); // Sync: task.RunSynchronously();
            return task;
        }

        public Task<bool> StatusTask(bool remote = true, bool full = true)
        {
            return StartTask(() => Status(remote, full));
        }

        public Task<bool> StatusTask(IEnumerable<string> assets, bool remote = true)
        {
            assets = new List<string>(assets);
            return StartTask(() => Status(assets, remote));
        }

        public Task<bool> UpdateTask(IEnumerable<string> assets = null, bool force = true)
        {
            if (assets != null) assets = new List<string>(assets);
            return StartTask(() => Update(assets, force));
        }

        public Task<bool> AddTask(IEnumerable<string> assets)
        {
            assets = new List<string>(assets);
            return StartTask(() => Add(assets));
        }

        public Task<bool> CommitTask(IEnumerable<string> assets, string commitMessage = "")
        {
            assets = new List<string>(assets);
            FlushFiles();
            return StartTask(() => Commit(assets, commitMessage));
        }

        public Task<bool> GetLockTask(IEnumerable<string> assets, bool force = false)
        {
            assets = new List<string>(assets);
            return StartTask(() => GetLock(assets, force));
        }

        public Task<bool> RevertTask(IEnumerable<string> assets)
        {
            assets = new List<string>(assets);
            FlushFiles();
            return StartTask(() => Revert(assets));
        }
        #endregion

        #region IVersionControlCommands

        public bool IsReady()
        {
            return VCSettings.VCEnabled && vcc.IsReady();
        }
        public void SetWorkingDirectory(string workingDirectory)
        {
            vcc.SetWorkingDirectory(workingDirectory);
        }
        public void SetUserCredentials(string userName, string password)
        {
            vcc.SetUserCredentials(userName, password);
        }
        public VersionControlStatus GetAssetStatus(string assetPath)
        {
            return vcc.GetAssetStatus(assetPath);
        }
        public IEnumerable<string> GetFilteredAssets(Func<string, VersionControlStatus, bool> filter)
        {
            return RemoveMetaPostFix(vcc.GetFilteredAssets(filter));
        }
        public bool Status(bool remote = true, bool full = true)
        {
            return HandleExceptions(() =>
            {
                bool result = vcc.Status(remote, full);
                if (result)
                {
                    OnNextUpdate.Do(() => AssetDatabase.Refresh());
                    OnNextUpdate.Do(OnStatusComplete);
                }
                return result;
            });
        }

        public bool Status(IEnumerable<string> assets, bool remote = false)
        {
            return HandleExceptions(() =>
            {
                var withMeta = AddMeta(assets, true);
                bool result = vcc.Status(withMeta, remote);
                if (result)
                {
                    OnNextUpdate.Do(() => AssetDatabase.Refresh());
                    OnNextUpdate.Do(OnStatusComplete);
                }
                return result;
            });
        }

        public bool Update(IEnumerable<string> assets, bool force = true)
        {
            return HandleExceptions(() => vcc.Update(assets, force) && RequestStatus());
        }

        public bool Commit(IEnumerable<string> assets, string commitMessage = "")
        {
            return HandleExceptions(() =>
            {
                FlushFiles();
                assets = AddMeta(assets);
                return Status(assets) && vcc.Commit(assets, commitMessage) && RequestStatus();
            });
        }
        public bool Add(IEnumerable<string> assets)
        {
            return HandleExceptions(() =>
            {
                assets = AddMeta(assets, true);
                return vcc.Add(assets) && RequestStatus();
            });
        }
        public bool Revert(IEnumerable<string> assets)
        {
            return HandleExceptions(() =>
            {
                FlushFiles();
                Status(assets);
                assets = AddMeta(assets);
                bool revertResult = vcc.Revert(assets);
                vcc.ChangeListRemove(assets);
                if (revertResult) vcc.ReleaseLock(assets);
                RequestStatus();
                return revertResult;
            });
        }
        public bool Delete(IEnumerable<string> assets, bool force = false)
        {
            return HandleExceptions(() =>
            {
                var deleteAssets = new List<string>();
                foreach (string assetIt in assets)
                {
                    if (GetAssetStatus(assetIt).fileStatus != VCFileStatus.Unversioned)
                    {
                        deleteAssets.Add(assetIt + ".meta");
                        deleteAssets.Add(assetIt);
                    }
                    else
                    {
                        if (File.Exists(assetIt + ".meta")) File.Delete(assetIt + ".meta");
                        if (File.Exists(assetIt)) File.Delete(assetIt);
                        if (Directory.Exists(assetIt)) Directory.Delete(assetIt);
                    }
                }
                return vcc.Delete(deleteAssets, force) && RequestStatus();
            });
        }
        public bool GetLock(IEnumerable<string> assets, bool force = false)
        {
            return HandleExceptions(() => vcc.GetLock(assets, force) && vcc.ChangeListRemove(assets) && RequestStatus());
        }
        public bool ReleaseLock(IEnumerable<string> assets)
        {
            return HandleExceptions(() => vcc.ReleaseLock(assets) && RequestStatus());
        }
        public bool ChangeListAdd(IEnumerable<string> assets, string changelist)
        {
            return HandleExceptions(() => vcc.ChangeListAdd(assets, changelist) && RequestStatus());
        }
        public bool ChangeListRemove(IEnumerable<string> assets)
        {
            return HandleExceptions(() => vcc.ChangeListRemove(assets) && RequestStatus());
        }
        public bool Checkout(string url, string path = "")
        {
            return HandleExceptions(() => vcc.Checkout(url, path) && RequestStatus());
        }
        public bool Resolve(IEnumerable<string> assets, ConflictResolution conflictResolution)
        {
            return HandleExceptions(() => vcc.Resolve(assets, conflictResolution) && RequestStatus());
        }
        public bool Move(string from, string to)
        {
            return HandleExceptions(() =>
                {
                    FlushFiles();
                    return vcc.Move(from, to) && vcc.Move(from + ".meta", to + ".meta") && RequestStatus();
                });
        }
        public string GetBasePath(string assetPath)
        {
            return vcc.GetBasePath(assetPath);
        }
        public bool CleanUp()
        {
            return HandleExceptions(() => vcc.CleanUp() && RequestStatus());
        }
        public void ClearDatabase()
        {
            vcc.ClearDatabase();
        }
        public event Action<string> ProgressInformation;
        public event Action StatusCompleted;

        private void OnStatusComplete() { if (StatusCompleted != null) StatusCompleted(); }

        public bool CommitDialog(IEnumerable<string> assets, bool showUserConfirmation = false, string commitMessage = "")
        {
            int initialAssetCount = assets.Count();
            if (initialAssetCount == 0) return true;

            assets = AddFilesInFolders(assets);
            assets = AddFolders(assets);
            var dependencies = GetDependencies(assets);
            dependencies = AddFilesInFolders(dependencies);
            dependencies = AddFolders(dependencies);
            dependencies = dependencies.Concat(AddDeletedInFolders(assets));

            if (assets.Contains(EditorApplication.currentScene))
            {
                EditorApplication.SaveCurrentSceneIfUserWantsTo();
            }
            if (showUserConfirmation || initialAssetCount < (assets.Count() + dependencies.Count()))
            {
                return OpenCommitDialogWindow(assets, dependencies);
            }
            return Commit(assets, commitMessage);

        }

        public void BypassRevision(IEnumerable<string> assets)
        {
            vcc.ChangeListAdd(assets, "bypass");
            RequestStatus();
        }
        #endregion

    }
}