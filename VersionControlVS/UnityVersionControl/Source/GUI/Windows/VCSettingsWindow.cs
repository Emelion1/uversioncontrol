// Copyright (c) <2012> <Playdead>
// This file is subject to the MIT License as seen in the trunk of this repository
// Maintained by: <Kristian Kjems> <kristian.kjems+UnitySVN@gmail.com>
using System;
using UnityEngine;
using UnityEditor;

namespace VersionControl.UserInterface
{
    [Serializable]
    internal class VCSettingsWindow : EditorWindow
    {
        //[MenuItem("UVC/Settings")]
        public static void Init()
        {
            GetWindow(typeof (VCSettingsWindow), false, "Version Control Settings");
        }

        [SerializeField] private readonly VCSettingsGUI settingsGUI = new VCSettingsGUI();

        private void OnEnable()
        {
            minSize = new Vector2(200, 200);
        }

        private void OnGUI()
        {
            settingsGUI.DrawGUI();
        }

    }

    [Serializable]
    internal class VCSettingsGUI
    {
        private bool VCEnabled
        {
            get { return VCSettings.VCEnabled; }
            set { VCSettings.VCEnabled = value; }
        }

        public void DrawGUI()
        {
            GUILayout.Label("Lock Settings", EditorStyles.boldLabel);
            using (GUILayoutHelper.VerticalIdented(14))
            {
                VCSettings.LockScenes = GUILayout.Toggle(VCSettings.LockScenes, new GUIContent("Scene Lock", "Version Control allowed to lock GUI for scenes which are not " + Terminology.getlock + "\nDefault: On"));
                VCSettings.LockPrefabs = GUILayout.Toggle(VCSettings.LockPrefabs, new GUIContent("Prefab Lock", "Version Control allowed to lock GUI for prefabs which are not " + Terminology.getlock + "\nDefault: Off"));
                VCSettings.LockMaterials = GUILayout.Toggle(VCSettings.LockMaterials, new GUIContent("Material Lock", "Version Control allowed to lock GUI for materials which are not " + Terminology.getlock + "\nDefault: On"));
            }
            GUILayout.Label("GUI Settings", EditorStyles.boldLabel);
            using (GUILayoutHelper.VerticalIdented(14))
            {
                VCSettings.SceneviewGUI = GUILayout.Toggle(VCSettings.SceneviewGUI, new GUIContent("Scene GUI", "Should Version Control GUI in Scene be active\nDefault: On"));
                VCSettings.HierarchyIcons = GUILayout.Toggle(VCSettings.HierarchyIcons, new GUIContent("Hierachy Icons", "Show Version Control controls in hierachy view\nDefault: On"));
                VCSettings.ProjectIcons = GUILayout.Toggle(VCSettings.ProjectIcons, new GUIContent("Project Icons", "Show Version Control controls in project view\nDefault: On"));
            }
            GUILayout.Label("Debug", EditorStyles.boldLabel);
            using (GUILayoutHelper.VerticalIdented(14))
            {
                using (GUILayoutHelper.Horizontal())
                {
                    VCSettings.BugReport = GUILayout.Toggle(VCSettings.BugReport, new GUIContent("Bug Reports", "Send a bug report to Fogbugz when an error occurs\nDefault: On"));
                    VCSettings.BugReportMode = (VCSettings.EBugReportMode) EditorGUILayout.EnumPopup(VCSettings.BugReportMode);
                }
                VCSettings.Logging = GUILayout.Toggle(VCSettings.Logging, new GUIContent("Logging", "Output logs from Version Control to Unity console\nDefault: Off"));
            }
        }
    }
}