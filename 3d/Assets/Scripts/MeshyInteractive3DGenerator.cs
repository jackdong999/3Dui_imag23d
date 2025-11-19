using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using GLTFast;
using TMPro;

public class MeshyInteractive3DGenerator : MonoBehaviour
{
    [Header("🔑 Meshy API (TEST ONLY: hardcoded)")]
    [Tooltip("For testing only. Move the key to a secure server-side proxy before shipping.")]
    public string meshyApiKey = "PASTE_YOUR_MESHY_API_KEY_HERE";

    [Header("UI (drag references from your Canvas)")]
    public Button pickAndGenerateButton;
    public TextMeshProUGUI statusTMP;

    [Header("Spawn Settings")]
    public Transform spawnParent;           // Optional parent for the spawned model
    public bool placeInFrontOfHmd = true;   // If no parent, place in front of the HMD
    public float placeDistance = 0.7f;      // Meters in front of the HMD
    public float targetSizeMeters = 0.3f;   // Normalize longest dimension to this size

    [Header("Interaction (Hand Grab)")]
    [Tooltip("Prefab that already has Grab / HandGrab / Grabbable components (Meta XR Interaction SDK).")]
    public GameObject handGrabWrapperPrefab;

    [Header("Persistence")]
    [Tooltip("App 启动时自动加载最后一次保存的模型（如果有）。")]
    public bool loadLastOnStart = true;

    [Header("Debug")]
    public bool autoOpenPickerOnStart = false; // Auto-open file picker on start (device only)

    // ==== Meshy OpenAPI ====
    const string BaseUrl = "https://api.meshy.ai/openapi/v1";

    // 保存本地 GLB 路径的 key & 列表文件
    const string LastGlbPathKey = "Meshy_LastGlbPath";
    const string ModelsDirName = "meshy_models";
    const string ModelListFileName = "meshy_model_list.json";

    [Serializable] class CreateResp { public string result; }
    [Serializable] class ModelUrls { public string glb; }
    [Serializable]
    class TaskResp
    {
        public string id;
        public string status;
        public int progress;
        public ModelUrls model_urls;
    }

    [Serializable]
    class SavedModelList
    {
        public List<string> files = new List<string>();
    }

    void Awake()
    {
        if (pickAndGenerateButton != null)
            pickAndGenerateButton.onClick.AddListener(OnPickButton);
    }
    public void OnLoadAllButton()
    {
        // 按钮点击时调用，内部直接调用我们写好的加载函数
        LoadAllSavedModels();
    }

    // 把 glTFast 的 Shader 换成 URP 的 Lit / Unlit
    void ConvertGlTFastMaterialsToURP(GameObject root)
    {
        // 找 URP 的标准 Shader（前提是你的项目确实是 URP 管线）
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");

        if (litShader == null)
        {
            Debug.LogWarning("[Meshy] URP Lit shader not found, skip material conversion.");
            return;
        }

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || m.shader == null) continue;

                string shaderName = m.shader.name;

                // glTFast 自带的 shader / ShaderGraph 名里一般都会带 "glTF"
                if (shaderName.Contains("glTF"))
                {
                    bool isUnlit =
                        shaderName.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0;

                    Shader target = (isUnlit && unlitShader != null) ? unlitShader : litShader;

                    // 因为 glTFast 把属性名做得跟 URP Lit 一样，所以直接换 shader，贴图/颜色会跟着迁移
                    m.shader = target;
                }
            }
        }
    }


    void DumpMaterials(GameObject root)
    {
        // 进来先打一条，确认函数被调用
        Debug.Log("[MeshyTest] DumpMaterials CALLED");

        var rends = root.GetComponentsInChildren<Renderer>(true);
        Debug.Log($"[MeshyDump] Renderer count = {rends.Length}");

        foreach (var r in rends)
        {
            Debug.Log($"[MeshyDump] Renderer = {r.name}");

            var mats = r.sharedMaterials;
            Debug.Log($"[MeshyDump]   Material count on {r.name} = {mats.Length}");

            foreach (var mat in mats)
            {
                if (mat == null)
                {
                    Debug.Log("[MeshyDump]   Mat = <null>");
                    continue;
                }

                // 一定要这样写，不能 mat.shader ? ...
                var shaderName = (mat.shader != null) ? mat.shader.name : "<no shader>";

                Texture mainTex = null;
                if (mat.HasProperty("_BaseMap"))
                    mainTex = mat.GetTexture("_BaseMap");
                else if (mat.HasProperty("_MainTex"))
                    mainTex = mat.GetTexture("_MainTex");

                Color color = Color.white;
                if (mat.HasProperty("_BaseColor"))
                    color = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color"))
                    color = mat.GetColor("_Color");

                string texName = (mainTex != null) ? mainTex.name : "null";

                Debug.Log(
                    $"[MeshyDump]   Mat={mat.name}, shader={shaderName}, mainTex={texName}, color={color}"
                );
            }
        }
    }



    IEnumerator Start()
    {
        // 1) 启动时先试着从本地读“最后一个模型”
        if (loadLastOnStart)
        {
            string lastPath = PlayerPrefs.GetString(LastGlbPathKey, string.Empty);
            if (!string.IsNullOrEmpty(lastPath) && File.Exists(lastPath))
            {
                Log("Loading last saved model...");
                var t = LoadGlbFromFile(lastPath);
                while (!t.IsCompleted) yield return null;

                if (!t.IsFaulted)
                {
                    var rawModel = t.Result;
                    SetupSpawnedModel(rawModel);
                    Log("Last saved model loaded.");
                }
                else
                {
                    Log("Failed to load last saved model: " + t.Exception);
                }
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (autoOpenPickerOnStart)
        {
            yield return new WaitForSeconds(0.8f);
            OnPickButton();
        }
#else
        yield return null;
#endif
    }

    public void OnPickButton()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (NativeFilePicker.IsFilePickerBusy()) return;
        NativeFilePicker.PickFile(OnFilePicked, new string[] { "image/*" });
#else
        Log("Please test on a Quest device (the system file picker is not available in the Editor).");
#endif
    }

    void OnFilePicked(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Log("Selection cancelled.");
            return;
        }
        StartCoroutine(Pipeline(path));
    }

    // ============ Main pipeline ============
    IEnumerator Pipeline(string imagePath)
    {
        Log("Reading image...");
        byte[] bytes = null;
        try
        {
            bytes = File.ReadAllBytes(imagePath);
        }
        catch (Exception e)
        {
            Log("Read failed: " + e.Message);
            yield break;
        }

        string dataUri = ToDataUri(bytes, imagePath);

        // 1) Create job
        Log("Creating Meshy job...");
        string taskId = null;
        yield return CreateImageTo3DTask(
            dataUri,
            id => taskId = id,
            err => Log(err)
        );
        if (string.IsNullOrEmpty(taskId)) yield break;
        Log($"Job created: {taskId}. Polling...");

        // 2) Poll until ready
        string glbUrl = null;
        yield return PollUntilReady(
            taskId,
            (status, progress, url) =>
            {
                Log($"Status: {status}   Progress: {progress}%");
                if (!string.IsNullOrEmpty(url)) glbUrl = url;
            },
            err => Log(err),
            3f
        );
        if (string.IsNullOrEmpty(glbUrl))
        {
            Log("No GLB download URL returned.");
            yield break;
        }

        // 2.5) 把 GLB 下载到本地（生成一个新的 glb 文件，并加入列表）
        StartCoroutine(SaveGlbAndRegister(glbUrl));

        // 3) 直接从网络 URL 加载（带贴图）
        Log("Loading model from URL...");
        var t = LoadGlbFromUrl(glbUrl);
        while (!t.IsCompleted) yield return null;
        if (t.IsFaulted)
        {
            Log("Load failed: " + t.Exception);
            Debug.LogException(t.Exception);
            yield break;
        }

        var rawModel = t.Result;

        // 4) 统一做：归一化尺寸 + 放到面前 + HandGrab 包装（并隐藏 wrapper 自己的 Cube）
        SetupSpawnedModel(rawModel);

        Log("Done! Model loaded (hand grab + multi-save).");
    }

    // ============ HTTP ============
    IEnumerator CreateImageTo3DTask(string dataUri, Action<string> onOk, Action<string> onErr)
    {
        string json =
            $"{{\"image_url\":\"{dataUri}\",\"enable_pbr\":true,\"should_remesh\":true,\"should_texture\":true}}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest($"{BaseUrl}/image-to-3d", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onErr?.Invoke($"Create failed: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            var resp = JsonUtility.FromJson<CreateResp>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(resp?.result))
            {
                onErr?.Invoke("Create succeeded but no taskId returned.");
                yield break;
            }
            onOk?.Invoke(resp.result);
        }
    }

    IEnumerator PollUntilReady(string taskId, Action<string, int, string> onProgress, Action<string> onErr, float intervalSec)
    {
        while (true)
        {
            using (var req = UnityWebRequest.Get($"{BaseUrl}/image-to-3d/{taskId}"))
            {
                req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
                req.downloadHandler = new DownloadHandlerBuffer();
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"Poll failed: {req.responseCode} {req.error}");
                    yield break;
                }

                var task = JsonUtility.FromJson<TaskResp>(req.downloadHandler.text);
                string status = task?.status ?? "UNKNOWN";
                int progress = task?.progress ?? 0;
                string glb = task?.model_urls?.glb;

                onProgress?.Invoke(status, progress, glb);

                if (status == "SUCCEEDED" && !string.IsNullOrEmpty(glb))
                    yield break;
                if (status == "FAILED" || status == "CANCELED")
                {
                    onErr?.Invoke($"Task {status}");
                    yield break;
                }
            }

            yield return new WaitForSeconds(intervalSec);
        }
    }

    // ============ 保存多个 GLB ============

    string GetModelsDir()
    {
        return Path.Combine(Application.persistentDataPath, ModelsDirName);
    }

    string GetModelListPath()
    {
        return Path.Combine(GetModelsDir(), ModelListFileName);
    }

    SavedModelList LoadModelList()
    {
        string listPath = GetModelListPath();
        if (!File.Exists(listPath))
            return new SavedModelList();

        try
        {
            string json = File.ReadAllText(listPath);
            var list = JsonUtility.FromJson<SavedModelList>(json);
            return list ?? new SavedModelList();
        }
        catch
        {
            return new SavedModelList();
        }
    }

    void SaveModelList(SavedModelList list)
    {
        string dir = GetModelsDir();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string listPath = GetModelListPath();
        string json = JsonUtility.ToJson(list, true);
        File.WriteAllText(listPath, json);
    }

    IEnumerator SaveGlbAndRegister(string url)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MeshyInteractive3DGenerator] SaveGlb failed: {req.responseCode} {req.error}");
                yield break;
            }

            byte[] data = req.downloadHandler.data;
            string dir = GetModelsDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 用时间戳生成不重复的文件名
            string fileName = $"meshy_{DateTime.Now:yyyyMMdd_HHmmss}.glb";
            string filePath = Path.Combine(dir, fileName);
            File.WriteAllBytes(filePath, data);

            // 更新列表
            var list = LoadModelList();
            list.files.Add(filePath);
            SaveModelList(list);

            // 仍然记住“最后一个模型”方便下次快速加载
            PlayerPrefs.SetString(LastGlbPathKey, filePath);
            PlayerPrefs.Save();

            Debug.Log("[MeshyInteractive3DGenerator] Saved GLB to: " + filePath);
        }
    }

    // ============ glTFast: load from URL / File ============

    async Task<GameObject> LoadGlbFromUrl(string glbUrl)
    {
        var gltf = new GltfImport();

        bool ok = await gltf.Load(glbUrl);
        if (!ok) throw new Exception("glTFast Load(url) failed.");

        var root = new GameObject("MeshyModel");
        bool instOk = await gltf.InstantiateMainSceneAsync(root.transform);
        if (!instOk) throw new Exception("InstantiateMainSceneAsync failed.");

        return root;
    }

    async Task<GameObject> LoadGlbFromFile(string localPath)
    {
        byte[] bytes = File.ReadAllBytes(localPath);

        var gltf = new GltfImport();
        bool ok = await gltf.LoadGltfBinary(bytes, new Uri("file://" + localPath));
        if (!ok) throw new Exception("glTFast LoadGltfBinary failed.");

        var root = new GameObject("MeshyModel");
        bool instOk = await gltf.InstantiateMainSceneAsync(root.transform);
        if (!instOk) throw new Exception("InstantiateMainSceneAsync failed.");

        return root;
    }

    // ============ 一次性加载所有历史模型（可选调用） ============

    public void LoadAllSavedModels()
    {
        StartCoroutine(CoLoadAllSavedModels());
    }

    IEnumerator CoLoadAllSavedModels()
    {
        var list = LoadModelList();
        if (list.files == null || list.files.Count == 0)
        {
            Log("No saved models found.");
            yield break;
        }

        Log($"Loading {list.files.Count} saved models...");

        foreach (var path in list.files)
        {
            if (!File.Exists(path))
                continue;

            var t = LoadGlbFromFile(path);
            while (!t.IsCompleted) yield return null;

            if (!t.IsFaulted)
            {
                var rawModel = t.Result;
                SetupSpawnedModel(rawModel);
            }
        }

        Log("All saved models loaded.");
    }

    // 统一处理：归一化尺寸 + HandGrab 包装 + 放到玩家前方 + 隐藏壳里的可见方块
    // 统一处理：归一化尺寸 + HandGrab 包装 + 放到玩家前方 + 隐藏壳里的可见方块
    void SetupSpawnedModel(GameObject rawModel)
    {
        Debug.Log("[MeshyTest] SetupSpawnedModel CALLED");

        var b0 = ComputeWorldBounds(rawModel);
        Debug.Log($"[MeshyInteractive3DGenerator] Bounds before normalize: {b0.size}");

        // 归一化尺寸
        NormalizeBounds(rawModel, targetSizeMeters);

        // 把所有 glTF shader 换成 URP Lit / Unlit
        ConvertGlTFastMaterialsToURP(rawModel);

        var b1 = ComputeWorldBounds(rawModel);
        Debug.Log($"[MeshyInteractive3DGenerator] Bounds after normalize: {b1.size}");

        if (handGrabWrapperPrefab != null)
        {
            var wrapper = Instantiate(handGrabWrapperPrefab);
            wrapper.name = "MeshyHandGrabbable";

            rawModel.transform.SetParent(wrapper.transform, worldPositionStays: false);
            rawModel.transform.localPosition = Vector3.zero;
            rawModel.transform.localRotation = Quaternion.identity;

            HideWrapperVisuals(wrapper, rawModel);

            PlaceObject(wrapper.transform);
            EnsureBoundsCollider(wrapper.gameObject);
        }
        else
        {
            PlaceObject(rawModel.transform);
            EnsureBoundsCollider(rawModel.gameObject);
        }

        // 打印材质信息
        DumpMaterials(rawModel);
    }



    // 隐藏 wrapper 里所有“不属于模型本身”的 Renderer（包括 Cube）
    void HideWrapperVisuals(GameObject wrapper, GameObject rawModel)
    {
        var renderers = wrapper.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (!r.transform.IsChildOf(rawModel.transform))
            {
                r.enabled = false;
            }
        }
    }

    // ============ Placement / Utils ============

    void PlaceObject(Transform t)
    {
        if (spawnParent != null)
        {
            t.SetParent(spawnParent, true);
            return;
        }

        var hmd = GetHmdTransform();
        if (hmd != null && placeInFrontOfHmd)
        {
            t.position = hmd.position + hmd.forward * placeDistance;
            t.rotation = Quaternion.LookRotation(hmd.forward, Vector3.up);
        }
        else
        {
            t.position = new Vector3(0, 1.2f, 1.5f);
            t.rotation = Quaternion.identity;
            Debug.LogWarning("[MeshyInteractive3DGenerator] HMD not found; placed at fallback position.");
        }

        SetLayerRecursively(t.gameObject, LayerMask.NameToLayer("Default"));

        var cam = GetHmdCamera();
        if (cam && (cam.cullingMask & (1 << t.gameObject.layer)) == 0)
        {
            Debug.LogWarning("[MeshyInteractive3DGenerator] Model layer hidden by camera; forcing Default layer.");
            SetLayerRecursively(t.gameObject, 0);
        }
    }

    Transform GetHmdTransform()
    {
        if (Camera.main) return Camera.main.transform;

        var go =
            GameObject.Find("OVRHmd") ??
            GameObject.Find("CenterEyeAnchor") ??
            GameObject.Find("Main Camera");
        if (go) return go.transform;

        var anyCam = GetHmdCamera();
        return anyCam ? anyCam.transform : null;
    }

    Camera GetHmdCamera()
    {
        if (Camera.main) return Camera.main;
        var cams = GameObject.FindObjectsOfType<Camera>(true);
        foreach (var c in cams)
        {
            if (c.enabled) return c;
        }
        return cams.Length > 0 ? cams[0] : null;
    }

    void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform t in go.transform)
            SetLayerRecursively(t.gameObject, layer);
    }

    static Bounds ComputeWorldBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        Bounds? b = null;
        foreach (var r in rends)
        {
            if (!b.HasValue) b = r.bounds;
            else
            {
                var bb = b.Value;
                bb.Encapsulate(r.bounds);
                b = bb;
            }
        }
        return b ?? new Bounds(go.transform.position, Vector3.one * 0.1f);
    }

    static void NormalizeBounds(GameObject go, float targetSizeMeters)
    {
        var b = ComputeWorldBounds(go);
        float max = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (max <= 1e-6f) return;
        float scale = targetSizeMeters / max;
        go.transform.localScale *= scale;

        // Sit on ground (bottom at y = 0)
        b = ComputeWorldBounds(go);
        go.transform.position -= new Vector3(0, b.min.y, 0);
    }

    void EnsureBoundsCollider(GameObject root)
    {
        if (root.GetComponentInChildren<Collider>() != null)
            return;

        var b = ComputeWorldBounds(root);

        var col = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(b.center);
        col.size = b.size;
    }

    void Log(string s)
    {
        Debug.Log("[MeshyInteractive3DGenerator] " + s);
        if (statusTMP) statusTMP.text = s;
    }

    public static string ToDataUri(byte[] bytes, string filePath)
    {
        string ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        string mime = (ext == ".png") ? "image/png" : "image/jpeg";
        string b64 = Convert.ToBase64String(bytes);
        return $"data:{mime};base64,{b64}";
    }
}
