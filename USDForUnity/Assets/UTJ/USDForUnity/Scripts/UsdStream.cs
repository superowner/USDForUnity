using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UTJ.USD
{
    [ExecuteInEditMode]
    public class UsdStream : MonoBehaviour, ISerializationCallbackReceiver
    {
        #region types
        // just for serialize int[][] (Unity doesn't serialize array of arrays)
        [Serializable]
        public class VariantSelection
        {
            public int[] selections;
        }
        #endregion


        #region fields
        [SerializeField] public DataPath m_path;
        [SerializeField] public usdi.ImportSettings m_importSettings = new usdi.ImportSettings();
        [SerializeField] public TimeUnit m_timeUnit = new TimeUnit();
        [SerializeField] public double m_time;

        [Header("Debug")]
#if UNITY_EDITOR
        [SerializeField] public bool m_forceSingleThread = false;
        [SerializeField] public bool m_detailedLog = false;
        bool m_isCompiling = false;
#endif

        [HideInInspector][SerializeField] string[] m_variantSelections_keys;
        [HideInInspector][SerializeField] VariantSelection[] m_variantSelections_values;
        [HideInInspector][SerializeField] string[] m_perObjectSettings_keys;
        [HideInInspector][SerializeField] usdi.ImportSettings[] m_perObjectSettings_values;
        Dictionary<string, VariantSelection> m_variantSelections = new Dictionary<string, VariantSelection>();
        Dictionary<string, usdi.ImportSettings> m_perObjectSettings = new Dictionary<string, usdi.ImportSettings>();

        List<UsdSchema> m_schemas = new List<UsdSchema>();
        Dictionary<string, UsdSchema> m_schemaLUT = new Dictionary<string, UsdSchema>();

        usdi.Context m_ctx;
        bool m_requestForceUpdate;
        bool m_requestReload;
        double m_prevUpdateTime = Double.NaN;
        #endregion


        #region properties
        public DataPath usdPath
        {
            get { return m_path; }
        }
        public usdi.Context usdiContext
        {
            get { return m_ctx; }
        }
        public usdi.ImportSettings importSettings
        {
            get { return m_importSettings; }
            set { m_importSettings = value; }
        }
        public double playTime
        {
            get { return m_time; }
            set { m_time = value; }
        }
        public TimeUnit timeUnit
        {
            get { return m_timeUnit; }
            set { m_timeUnit = value; }
        }
#if UNITY_EDITOR
        public bool forceSingleThread
        {
            get { return m_forceSingleThread; }
            set { m_forceSingleThread = value; }
        }
#endif
        #endregion


        #region impl
        public void OnBeforeSerialize()
        {
            m_variantSelections_keys = m_variantSelections.Keys.ToArray();
            m_variantSelections_values = m_variantSelections.Values.ToArray();

            m_perObjectSettings_keys = m_perObjectSettings.Keys.ToArray();
            m_perObjectSettings_values = m_perObjectSettings.Values.ToArray();
        }
        public void OnAfterDeserialize()
        {
            // variant selections
            if (m_variantSelections_keys != null &&
                m_variantSelections_values != null &&
                m_variantSelections_keys.Length == m_variantSelections_values.Length)
            {
                int n = m_variantSelections_keys.Length;
                for (int i = 0; i < n; ++i)
                {
                    m_variantSelections[m_variantSelections_keys[i]] = m_variantSelections_values[i];
                }
            }
            m_variantSelections_keys = null;
            m_variantSelections_values = null;

            // per-object import settings
            if (m_perObjectSettings_keys != null &&
                m_perObjectSettings_values != null &&
                m_perObjectSettings_keys.Length == m_perObjectSettings_values.Length)
            {
                int n = m_perObjectSettings_keys.Length;
                for (int i = 0; i < n; ++i)
                {
                    m_perObjectSettings[m_perObjectSettings_keys[i]] = m_perObjectSettings_values[i];
                }
            }
            m_perObjectSettings_keys = null;
            m_perObjectSettings_values = null;
        }


        public void UsdSetVariantSelection(string primPath, int[] selection)
        {
            m_variantSelections[primPath] = new VariantSelection { selections = selection };
            UsdRequestReload();
        }

        public void UsdSetImportSettings(string primPath, ref usdi.ImportSettings settings)
        {
            m_perObjectSettings[primPath] = settings;
            UsdRequestForceUpdate();
        }
        public void UsdDeleteImportSettings(string primPath)
        {
            m_perObjectSettings.Remove(primPath);
            UsdRequestForceUpdate();
        }

        public void UsdRequestForceUpdate()
        {
            m_requestForceUpdate = true;
        }
        public void UsdRequestReload()
        {
            m_requestReload = true;
        }

        public UsdSchema UsdFindSchema(string primPath)
        {
            if(m_schemaLUT.ContainsKey(primPath))
            {
                return m_schemaLUT[primPath];
            }
            return null;
        }
        public UsdSchema UsdFindSchema(usdi.Schema s)
        {
            return UsdFindSchema(s.GetPath());
        }


        void UsdLog(string message)
        {
#if UNITY_EDITOR
            if (m_detailedLog)
            {
                Debug.Log(message);
            }
#endif
        }

        UsdSchema UsdCreateNode(usdi.Schema schema)
        {
            UsdSchema ret = null;
            if (ret == null)
            {
                var s = schema.AsPoints();
                if (s) ret = new UsdPoints();
            }
            if (ret == null)
            {
                var s = schema.AsMesh();
                if (s) ret = new UsdMesh();
            }
            if (ret == null)
            {
                var s = schema.AsCamera();
                if (s) ret = new UsdCamera();
            }
            if (ret == null)
            {
                // Xform must be latter because some schemas are subclass of Xform
                var s = schema.AsXform();
                if (s) ret = new UsdXform();
            }
            if (ret == null)
            {
                ret = new UsdSchema();
            }
            ret.nativeSchemaPtr = schema;
            ret.stream = this;
            return ret;
        }

        UsdSchema UsdFindOrCreateNode(Transform parent, usdi.Schema schema, ref bool created)
        {
            GameObject go = null;

            // find existing GameObject or create new one
            var name = schema.GetName();
            var child = parent.Find(name);
            if (child != null)
            {
                go = child.gameObject;
                created = false;
            }
            else if(go == null)
            {
                go = new GameObject();
                go.name = name;
                go.GetComponent<Transform>().SetParent(parent, false);
                created = true;
            }

            // create USD node
            UsdSchema ret = UsdCreateNode(schema);
            ret.gameObject = go;

            return ret;
        }

        void UsdConstructTree(Transform parent, usdi.Schema schema, Action<UsdSchema> node_handler)
        {
            if(!schema) { return; }

            bool created = false;
            var elem = UsdFindOrCreateNode(parent, schema, ref created);
            if (elem != null)
            {
                node_handler(elem);
            }

            var trans = elem == null ? parent : elem.GetComponent<Transform>();
            int num_children = schema.GetNumChildren();
            for(int ci = 0; ci < num_children; ++ci)
            {
                var child = schema.GetChild(ci);
                UsdConstructTree(trans, child, node_handler);
            }
        }

        void UsdConstructMasterTree(usdi.Schema schema, Action<UsdSchema> node_handler)
        {
            if (!schema) { return; }

            var elem = UsdCreateNode(schema);
            node_handler(elem);

            int num_children = schema.GetNumChildren();
            for (int ci = 0; ci < num_children; ++ci)
            {
                var child = schema.GetChild(ci);
                UsdConstructMasterTree(child, node_handler);
            }
        }

        void UsdConstructTrees()
        {
            List<GameObject> data = new List<GameObject>();
            foreach (var kvp in m_schemaLUT)
            {
                var e = kvp.Value;
                if (e.gameObject != null)
                {
                    var c = e.gameObject.GetComponent<UsdIComponent>();
                    if(c != null)
                    {
                        c.schema = null;
                        data.Add(e.gameObject);
                    }
                }
            }

            m_schemas = new List<UsdSchema>();
            m_schemaLUT = new Dictionary<string, UsdSchema>();

            // construct master tree
            {
                var nmasters = m_ctx.GetNumMasters();
                for (int i = 0; i < nmasters; ++i)
                {
                    UsdConstructMasterTree(m_ctx.GetMaster(i),
                        (e) =>
                        {
                            e.UsdOnLoad();
                            m_schemas.Add(e);
                            m_schemaLUT[e.primPath] = e;
                        });
                }
            }

            // construct non-master tree along with corresponding GameObject
            {
                var root = m_ctx.GetRoot();
                var nchildren = root.GetNumChildren();
                for (int i = 0; i < nchildren; ++i)
                {
                    UsdConstructTree(GetComponent<Transform>(), root.GetChild(i),
                        (e) =>
                        {
                            e.UsdOnLoad();
                            m_schemas.Add(e);
                            m_schemaLUT[e.primPath] = e;
                        });
                }
            }

            // delete GameObjects that lost corresponding USD schema (e.g. variant set has changed)
            foreach (var go in data)
            {
                if(go != null)
                {
                    var c = go.GetComponent<UsdIComponent>();
                    if (c != null && c.schema == null)
                    {
#if UNITY_EDITOR
                        Undo.DestroyObjectImmediate(go);
#else
                        DestroyImmediate(go);
#endif
                    }
                }
            }
        }


        void UsdApplyImportConfig()
        {
            m_ctx.SetImportSettings(ref m_importSettings);
        }

        bool UsdApplyVarianceSelections()
        {
            bool applied = false;
            foreach(var kvp in m_variantSelections)
            {
                var p = m_ctx.FindSchema(kvp.Key);
                var selections = kvp.Value.selections;
                int nvals = selections.Length;
                for (int s = 0; s < nvals; ++s)
                {
                    if(p.SetVariantSelection(s, selections[s]))
                    {
                        applied = true;
                    }
                }
            }
            return applied;
        }

        bool UsdLoad(string path)
        {
            return UsdLoad(new DataPath(path));
        }

        bool UsdLoad(DataPath path)
        {
            UsdUnload();

            m_path = path;
            m_path.readOnly = true;
            m_ctx = usdi.Context.Create(GetInstanceID());

            var fullpath = m_path.GetFullPath();
            if (!m_ctx.Open(fullpath))
            {
                m_ctx.Destroy();
                UsdLog("UsdStream: failed to load " + fullpath);
                return false;
            }

            // apply variant selections
            if(UsdApplyVarianceSelections())
            {
                m_ctx.RebuildSchemaTree();
            }
            UsdApplyImportConfig();

            UsdConstructTrees();

            // fill sample data with initial time
            m_requestForceUpdate = true;
            UsdPrepareSample();
            m_ctx.UpdateAllSamples(m_time);
            UsdSyncDataBegin();
            UsdSyncDataEnd();

            UsdLog("UsdStream: loaded " + fullpath);
            return true;
        }

        public void UsdUnload()
        {
            if(!m_ctx) { return; }

            int c = m_schemas.Count;
            for (int i = 0; i < c; ++i) { m_schemas[i].UsdOnUnload(); }

            m_schemas.Clear();
            m_schemaLUT.Clear();
            m_ctx.Destroy();

            UsdLog("UsdStream: unloaded " + m_path.GetFullPath());
        }

        public bool UsdSave()
        {
            return m_ctx.Save();
        }

        public bool UsdSaveAs(string path)
        {
            return m_ctx.SaveAs(path);
        }

        public void UsdDetachUsdComponents()
        {
            Action<UnityEngine.Object> deleter = (UnityEngine.Object o) => {
#if UNITY_EDITOR
                Undo.DestroyObjectImmediate(o);
#else
                DestroyImmediate(o);
#endif
            };

            int c = m_schemas.Count;
            foreach (var s in m_schemas)
            {
                var go = s.gameObject;
                if (go != null)
                {
                    var component = go.GetComponent<UsdIComponent>();
                    if (component != null)
                    {
                        deleter(component);
                    }
                }
            }
            deleter(this);
        }

        // possibly called from non-main thread
        void UsdPrepareSample()
        {
            // skip if update is not needed
            if(m_requestForceUpdate)
            {
                m_ctx.NotifyForceUpdate();
            }
            else if (m_time == m_prevUpdateTime)
            {
                return;
            }
            int c = m_schemas.Count;
            for (int i = 0; i < c; ++i)
                m_schemas[i].UsdPrepareSample();
        }

        void UsdSyncDataBegin()
        {
            if (!m_requestForceUpdate && m_time == m_prevUpdateTime) { return; }
            m_requestForceUpdate = false;

            int c = m_schemas.Count;
            for (int i = 0; i < c; ++i)
                m_schemas[i].UsdSyncDataBegin();
        }

        void UsdSyncDataEnd()
        {
            if (!m_requestForceUpdate && m_time == m_prevUpdateTime) { return; }
            m_requestForceUpdate = false;

            int c = m_schemas.Count;
            for (int i = 0; i < c; ++i)
                m_schemas[i].UsdSyncDataEnd();

            m_prevUpdateTime = m_time;
        }


        public void Load(string path)
        {
            if(!m_ctx)
            {
                m_path = new DataPath(path);
            }
        }
        public void Load(DataPath path)
        {
            if (!m_ctx)
            {
                m_path = path;
            }
        }
        public bool LoadImmediate(string path)
        {
            return UsdLoad(path);
        }
        public bool LoadImmediate(DataPath path)
        {
            return UsdLoad(path);
        }
        #endregion


        #region callbacks
        void OnApplicationQuit()
        {
            usdi.FinalizePlugin();
        }

        void Awake()
        {
            usdi.InitializePluginPass1();
            usdi.InitializePluginPass2();
        }

        void OnDestroy()
        {
            UsdUnload();
        }

        void Start()
        {
            UsdLoad(m_path);
        }

        void OnEnable()
        {
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                UsdUnload();
            }
#endif
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            UsdRequestForceUpdate();
        }
#endif

        void Update()
        {
            if(!m_ctx)
                return;

            UsdPrepareSample();
            m_ctx.UpdateAllSamples(m_time);
        }

        void LateUpdate()
        {
            if (!m_ctx)
                return;

            UsdSyncDataBegin();
            UsdSyncDataEnd();
        }
        #endregion
    }

}
