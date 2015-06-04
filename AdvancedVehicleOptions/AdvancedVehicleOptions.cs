﻿using ICities;
using UnityEngine;

using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Serialization;
using System.Reflection;

using ColossalFramework;
using ColossalFramework.Threading;
using ColossalFramework.UI;

namespace AdvancedVehicleOptions
{
    public class AdvancedVehicleOptions : LoadingExtensionBase, IUserMod
    {
        #region IUserMod implementation
        public string Name
        {
            get { return "Advanced Vehicle Options " + version; }
        }

        public string Description
        {
            get { return "Customize your vehicles"; }
        }

        public const string version = "1.1";
        #endregion

        private static VehicleOptions[] m_options;
        private static VehicleOptions[] m_default;
        private static GUI.UIMainPanel m_mainPanel;

        private static List<VehicleInfo> m_removeList = new List<VehicleInfo>();
        private static bool m_removeThreadRunning = false;

        private static List<VehicleInfo> m_removeParkedList = new List<VehicleInfo>();
        private static bool m_removeParkedThreadRunning = false;

        private static FieldInfo m_transferVehiclesDirty = typeof(VehicleManager).GetField("m_transferVehiclesDirty", BindingFlags.Instance | BindingFlags.NonPublic);

        private const string m_fileName = "AdvancedVehicleOptions.xml";
                
        #region LoadingExtensionBase overrides
        /// <summary>
        /// Called when the level (game, map editor, asset editor) is loaded
        /// </summary>
        public override void OnLevelLoaded(LoadMode mode)
        {
            // Is it an actual game ?
            if (mode != LoadMode.LoadGame && mode != LoadMode.NewGame)
                return;

            // Creating GUI
            UIView view = UIView.GetAView();

            try
            {
                m_mainPanel = (GUI.UIMainPanel)view.AddUIComponent(typeof(GUI.UIMainPanel));
                DebugUtils.Log("UIMainPanel created");
            }
            catch
            {
                DebugUtils.Warning("A new version of the mod has been installed." + Environment.NewLine +
                    "The game must be exited completely for changes to take effect." + Environment.NewLine +
                    "Until then the mod is disabled.");
                return;
            }

            CompileVehiclesList(true);
        }

        /// <summary>
        /// Called when the level is unloaded
        /// </summary>
        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();

            if (m_mainPanel == null) return;

            SaveConfig();
            RestoreOptions();

            DebugUtils.Log("Destroying UIMainPanel");
            GUI.UIUtils.DestroyDeeply(m_mainPanel);
        }

        public override void OnReleased()
        {
            base.OnReleased();

            if (m_mainPanel == null) return;

            SaveConfig();
            RestoreOptions();

            DebugUtils.Log("Destroying UIMainPanel");
            GUI.UIUtils.DestroyDeeply(m_mainPanel);
        }
        #endregion

        /// <summary>
        /// Load and apply the configuration file
        /// </summary>
        public static void LoadConfig(UIComponent source)
        {
            m_options = null; // Clear all options

            if (!File.Exists(m_fileName))
            {
                DebugUtils.Log("Configuration file not found. Creating new configuration file.");

                CreateConfig();
                ApplyOptions();

                // Update GUI list
                m_mainPanel.optionList = m_options;
                return;
            }

            XmlSerializer xmlSerializer = new XmlSerializer(typeof(VehicleOptions[]));
            VehicleOptions[] options = null;

            try
            {
                // Trying to Deserialize the configuration file
                using (FileStream stream = new FileStream(m_fileName, FileMode.Open))
                {
                    options = xmlSerializer.Deserialize(stream) as VehicleOptions[];
                }
            }
            catch (Exception e)
            {
                // Couldn't Deserialize (XML malformed?)
                DebugUtils.Warning("Couldn't load configuration (XML malformed?)");
                Debug.LogException(e);
                return;
            }

            if (options == null)
            {
                DebugUtils.Warning("Couldn't load configuration (vehicle list is null)");
                return;
            }
            
            // Remove unneeded options
            List<VehicleOptions> optionsList = new List<VehicleOptions>();

            for (uint i = 0; i < options.Length; i++)
            {
                VehicleInfo prefab = PrefabCollection<VehicleInfo>.FindLoaded(options[i].name);
                if (prefab != null)
                {
                    options[i].SetPrefab(prefab);
                    optionsList.Add(options[i]);
                }
            }

            m_options = optionsList.ToArray();

            // Checking for new vehicles
            CompileVehiclesList();

            // Update GUI list
            ApplyOptions();
            m_mainPanel.optionList = m_options;

            DebugUtils.Log("Configuration loaded");
            LogVehicleListSteamID();
        }

        /// <summary>
        /// Save the configuration file
        /// </summary>
        public static void SaveConfig()
        {
            if (m_options == null) return;

            try
            {
                using (FileStream stream = new FileStream(m_fileName, FileMode.OpenOrCreate))
                {
                    stream.SetLength(0); // Emptying the file !!!
                    XmlSerializer xmlSerializer = new XmlSerializer(typeof(VehicleOptions[]));
                    xmlSerializer.Serialize(stream, m_options);
                    DebugUtils.Log("Configuration saved");
                }
            }
            catch (Exception e)
            {
                DebugUtils.Warning("Couldn't save configuration at \"" + Directory.GetCurrentDirectory() + "\"");
                Debug.LogException(e);
            }
        }

        public static void ClearVehicles(VehicleOptions options, bool parked)
        {
            if (parked)
            {
                if (!m_removeParkedList.Contains(options.prefab))
                {
                    m_removeParkedList.Add(options.prefab);
                    if (!m_removeParkedThreadRunning) new EnumerableActionThread(ActionRemoveParked);
                }
            }
            else
            {
                if (!m_removeList.Contains(options.prefab))
                {
                    m_removeList.Add(options.prefab);
                    if (!m_removeThreadRunning) new EnumerableActionThread(ActionRemoveExisting);
                }
            }
        }

        public static IEnumerator ActionRemoveExisting(ThreadBase t)
        {
            m_removeThreadRunning = true;

            VehicleManager vehicleManager =  Singleton<VehicleManager>.instance;
            

            while(m_removeList.Count != 0)
            {
                VehicleInfo[] prefabs = m_removeList.ToArray();

                for (ushort i = 0; i < vehicleManager.m_vehicles.m_size; i++)
                {
                    if (vehicleManager.m_vehicles.m_buffer[i].Info == null) continue;
                    if (prefabs.Contains(vehicleManager.m_vehicles.m_buffer[i].Info))
                        vehicleManager.ReleaseVehicle(i);

                    if (i % 256 == 255) yield return i;
                }

                m_removeList.RemoveRange(0, prefabs.Count());
            }

            m_removeThreadRunning = false;
        }

        public static IEnumerator ActionRemoveParked(ThreadBase t)
        {
            m_removeParkedThreadRunning = true;

            VehicleManager vehicleManager = Singleton<VehicleManager>.instance;


            while (m_removeParkedList.Count != 0)
            {
                VehicleInfo[] prefabs = m_removeParkedList.ToArray();

                for (ushort i = 0; i < vehicleManager.m_parkedVehicles.m_size; i++)
                {
                    if (vehicleManager.m_parkedVehicles.m_buffer[i].Info == null) continue;
                    if (prefabs.Contains(vehicleManager.m_parkedVehicles.m_buffer[i].Info))
                        vehicleManager.ReleaseParkedVehicle(i);

                    if (i % 256 == 255) yield return i;
                }

                m_removeParkedList.RemoveRange(0, prefabs.Count());
            }

            m_removeParkedThreadRunning = false;
        }

        #region Apply
       
        public static void ApplyOptions()
        {
            if (m_options == null) return;

            for (int i = 0; i < m_options.Length; i++)
            {
                ApplyMaxSpeed(m_options[i]);
                ApplyColors(m_options[i]);
                ApplySpawning(m_options[i]);
                ApplyBackEngine(m_options[i]);
            }
        }

        public static void ApplyMaxSpeed(VehicleOptions options)
        {
            //if (!IsAICustom(options.prefab.m_vehicleAI))
                options.prefab.m_maxSpeed = options.maxSpeed;
            //else
                //DebugUtils.Log("Max Speed not applied: custom Vehicle AI detected");
        }

        public static void ApplyColors(VehicleOptions options)
        {
            options.prefab.m_color0 = options.color0;
            options.prefab.m_color1 = options.color1;
            options.prefab.m_color2 = options.color2;
            options.prefab.m_color3 = options.color3;
        }
        
        public static void ApplySpawning(VehicleOptions options)
        {
            options.prefab.m_placementStyle = options.enabled ? options.placementStyle : ItemClass.Placement.Manual;

            // Make transfer vehicle dirty
            m_transferVehiclesDirty.SetValue(Singleton<VehicleManager>.instance, true);
        }

        public static void ApplyBackEngine(VehicleOptions options)
        {
            // Is it a train?
            if (!options.hasTrailer || options.prefab.m_vehicleType != VehicleInfo.VehicleType.Train) return;

            VehicleInfo newTrailer = options.addBackEngine ? options.prefab : options.prefab.m_trailers[0].m_info;
            int last = options.prefab.m_trailers.Length - 1;

            if (options.prefab.m_trailers[last].m_info == newTrailer) return;

            options.prefab.m_trailers[last].m_info = newTrailer;

            if (options.addBackEngine)
                options.prefab.m_trailers[last].m_invertProbability = options.prefab.m_trailers[last].m_probability;
            else
                options.prefab.m_trailers[last].m_invertProbability = options.prefab.m_trailers[0].m_invertProbability;
            
            // TODO Apply on existing trains
            //EnumerableActionThread thread = new EnumerableActionThread(ActionAddBackEngine);
        }

        public static void RestoreOptions()
        {
            m_options = m_default;
            ApplyOptions();
            m_default = null;
        }

        #endregion

        private static void CreateConfig()
        {
            CompileVehiclesList();

            // Loading old mods saves
            List<OldMods.Vehicle> removerList = OldMods.VehicleRemoverMod.LoadConfig();
            List<OldMods.VehicleColorInfo> colorList = OldMods.VehicleColorChangerMod.LoadConfig();

            if (removerList != null || colorList != null)
            {
                for (int i = 0; i < m_options.Length; i++)
                {
                    if (removerList != null)
                    {
                        OldMods.Vehicle vehicle = removerList.Find((v) => { return v.name == m_options[i].name; });
                        if (vehicle.name == m_options[i].name) m_options[i].enabled = vehicle.enabled;
                    }

                    if (colorList != null)
                    {
                        OldMods.VehicleColorInfo vehicle = colorList.Find((v) => { return v.name == m_options[i].name; });
                        if (vehicle != null && vehicle.name == m_options[i].name)
                        {
                            m_options[i].color0 = vehicle.color0;
                            m_options[i].color1 = vehicle.color1;
                            m_options[i].color2 = vehicle.color2;
                            m_options[i].color3 = vehicle.color3;
                        }
                    }
                }
            }

            SaveConfig();
        }

        /// <summary>
        /// Check if new there are vehicles and add them to the options list
        /// </summary>
        private static void CompileVehiclesList(bool savedefault = false)
        {
            List<VehicleOptions> optionsList = new List<VehicleOptions>();
            if(m_options != null) optionsList.AddRange(m_options);

            for (uint i = 0; i < PrefabCollection<VehicleInfo>.PrefabCount(); i++)
            {
                VehicleInfo prefab = PrefabCollection<VehicleInfo>.GetPrefab(i);

                if (prefab == null || ContainsPrefab(prefab)) continue;

                // New vehicle
                VehicleOptions options = new VehicleOptions();
                options.SetPrefab(prefab);

                options.name = prefab.name;
                options.maxSpeed = prefab.m_maxSpeed;

                options.color0 = prefab.m_color0;
                options.color1 = prefab.m_color1;
                options.color2 = prefab.m_color2;
                options.color3 = prefab.m_color3;

                options.enabled = true;
                options.addBackEngine = false;

                if (prefab.m_vehicleType == VehicleInfo.VehicleType.Train && options.hasTrailer)
                {
                    options.addBackEngine = prefab.m_trailers[prefab.m_trailers.Length - 1].m_info == prefab;
                }

                optionsList.Add(options);
            }

            if(savedefault)
            {
                m_default = optionsList.ToArray();
                return;
            }

            DebugUtils.Log("Found " + (optionsList.Count - m_options.Length) + " new vehicle(s)");

            m_options = optionsList.ToArray();

        }

        private static bool ContainsPrefab(VehicleInfo prefab)
        {
            if (m_options == null) return false;
            for (int i = 0; i < m_options.Length; i++)
            {
                if (m_options[i].prefab == prefab) return true;
            }
            return false;
        }

        private static void LogVehicleListSteamID()
        {
            StringBuilder steamIDs = new StringBuilder("Vehicle Steam IDs : ");

            for (int i = 0; i < m_options.Length; i++)
            {
                if (m_options[i].name.Contains("."))
                {
                    steamIDs.Append(m_options[i].name.Substring(0, m_options[i].name.IndexOf(".") - 1));
                    steamIDs.Append(",");
                }
            }
            steamIDs.Length--;

            DebugUtils.Log(steamIDs.ToString());
        }

        private static bool IsAICustom(VehicleAI ai)
        {
            Type type = ai.GetType();
            return (type != typeof(AmbulanceAI) ||
                type != typeof(BusAI) ||
                type != typeof(CargoTruckAI) ||
                type != typeof(FireTruckAI) ||
                type != typeof(GarbageTruckAI) ||
                type != typeof(HearseAI) ||
                type != typeof(PassengerCarAI) ||
                type != typeof(PoliceCarAI));
        }
    }
}
