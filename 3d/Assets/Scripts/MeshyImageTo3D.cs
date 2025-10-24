// Meshy3DGenerator.cs
// Pick local photo -> Meshy Image-to-3D -> poll -> download GLB -> load with glTFast (no interaction)

using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using GLTFast;
using TMPro;

public class Meshy3DGenerator : MonoBehaviour
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
    public float placeDistance = 1.5f;      // Meters in front of the HMD
    public float targetSizeMeters = 0.3f;   // Normalize longest dimension to this size

    [Header("Debug")]
    public bool autoOpenPickerOnStart = false; // Auto-open file picker on start (device only)

    // ==== Meshy OpenAPI ====
    const string BaseUrl = "https://api.meshy.ai/openapi/v1";
    [Serializable] class CreateResp { public string result; }
    [Serializable] class ModelUrls { public string glb; }
    [Serializable] class TaskResp { public string id; public string status; public int progress; public ModelUrls model_urls; }

    void Awake()
    {
        if (pickAndGenerateButton != null)
            pickAndGenerateButton.onClick.AddListener(OnPickButton);
    }

    IEnumerator Start()
    {
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
        if (string.IsNullOrEmpty(path)) { Log("Selection cancelled."); return; }
        StartCoroutine(Pipeline(path));
    }

    // ============ Main pipeline ============
    IEnumerator Pipeline(string imagePath)
    {
        Log("Reading image...");
        byte[] bytes = null;
        try { bytes = File.ReadAllBytes(imagePath); }
        catch (Exception e) { Log("Read failed: " + e.Message); yield break; }

        string dataUri = ToDataUri(bytes, imagePath);

        // 1) Create job
        Log("Creating Meshy job...");
        string taskId = null;
        yield return CreateImageTo3DTask(dataUri, id => taskId = id, err => Log(err));
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
        if (string.IsNullOrEmpty(glbUrl)) { Log("No GLB download URL returned."); yield break; }

        // 3) Download GLB
        Log("Downloading GLB...");
        string localGlb = null;
        yield return DownloadGlb(glbUrl, p => localGlb = p, e => Log(e));
        if (string.IsNullOrEmpty(localGlb)) yield break;

        // 4) Load GLB
        Log("Loading model...");
        var t = LoadGlb(localGlb);
        while (!t.IsCompleted) yield return null;
        if (t.IsFaulted) { Log("Load failed: " + t.Exception?.Message); yield break; }

        var go = t.Result;

        // Bounds before normalize (for debugging)
        var b0 = ComputeWorldBounds(go);
        Debug.Log($"[Meshy3DGenerator] Bounds before normalize: {b0.size}");

        // Normalize size then place
        NormalizeBounds(go, targetSizeMeters);
        var b1 = ComputeWorldBounds(go);
        Debug.Log($"[Meshy3DGenerator] Bounds after normalize: {b1.size}");

        PlaceObject(go.transform);

        Log("Done! Model loaded (no interaction).");
    }

    // ============ HTTP ============
    IEnumerator CreateImageTo3DTask(string dataUri, Action<string> onOk, Action<string> onErr)
    {
        string json = $"{{\"image_url\":\"{dataUri}\",\"enable_pbr\":true,\"should_remesh\":true,\"should_texture\":true}}";
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
            if (string.IsNullOrEmpty(resp?.result)) { onErr?.Invoke("Create succeeded but no taskId returned."); yield break; }
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

                if (status == "SUCCEEDED" && !string.IsNullOrEmpty(glb)) yield break;
                if (status == "FAILED" || status == "CANCELED") { onErr?.Invoke($"Task {status}"); yield break; }
            }
            yield return new WaitForSeconds(intervalSec);
        }
    }

    IEnumerator DownloadGlb(string url, Action<string> onOk, Action<string> onErr)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onErr?.Invoke($"Download failed: {req.responseCode} {req.error}");
                yield break;
            }
            byte[] data = req.downloadHandler.data;
            string dir = Path.Combine(Application.persistentDataPath, "meshy_models");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"meshy_{DateTime.Now:yyyyMMdd_HHmmss}.glb");
            File.WriteAllBytes(file, data);
            onOk?.Invoke(file);
        }
    }

    // ============ glTFast ============
    async Task<GameObject> LoadGlb(string localGlbPath)
    {
        byte[] bytes = File.ReadAllBytes(localGlbPath);

        var gltf = new GltfImport();
        bool ok = await gltf.LoadGltfBinary(bytes, new Uri("file://" + localGlbPath));
        if (!ok) throw new Exception("glTFast LoadGltfBinary failed.");

        var root = new GameObject(Path.GetFileNameWithoutExtension(localGlbPath));
        bool instOk = await gltf.InstantiateMainSceneAsync(root.transform);
        if (!instOk) throw new Exception("InstantiateMainSceneAsync failed.");

        return root;
    }

    // ============ Placement / Utils ============

    // More robust placement that does not rely solely on Camera.main
    void PlaceObject(Transform t)
    {
        if (spawnParent != null)
        {
            t.SetParent(spawnParent, true);
            return;
        }

        var hmd = GetHmdTransform();
        if (hmd != null)
        {
            t.position = hmd.position + hmd.forward * placeDistance;
            t.rotation = Quaternion.LookRotation(hmd.forward, Vector3.up);
        }
        else
        {
            // Fallback if no HMD/camera found
            t.position = new Vector3(0, 1.2f, 1.5f);
            t.rotation = Quaternion.identity;
            Debug.LogWarning("[Meshy3DGenerator] HMD not found; placed at fallback position.");
        }

        // Ensure the model is on a visible layer (Default)
        SetLayerRecursively(t.gameObject, LayerMask.NameToLayer("Default"));

        // If the active camera hides this layer, force Default
        var cam = GetHmdCamera();
        if (cam && (cam.cullingMask & (1 << t.gameObject.layer)) == 0)
        {
            Debug.LogWarning("[Meshy3DGenerator] Model layer hidden by camera; forcing Default layer.");
            SetLayerRecursively(t.gameObject, 0);
        }
    }

    // Find a reliable HMD/camera transform even if no MainCamera tag exists
    Transform GetHmdTransform()
    {
        if (Camera.main) return Camera.main.transform;

        // Common Meta XR anchors / names
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
            if (c.enabled) return c; // first enabled camera
        }
        return cams.Length > 0 ? cams[0] : null;
    }

    void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform t in go.transform)
            SetLayerRecursively(t.gameObject, layer);
    }

    // Stronger bounds calculation (supports SkinnedMeshRenderer, empty cases)
    static Bounds ComputeWorldBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        Bounds? b = null;
        foreach (var r in rends)
        {
            if (!b.HasValue) b = r.bounds;
            else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
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

    void Log(string s)
    {
        Debug.Log("[Meshy3DGenerator] " + s);
        if (statusTMP) statusTMP.text = s;
    }

    public static string ToDataUri(byte[] bytes, string filePath)
    {
        string ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        string mime = (ext == ".png") ? "image/png" : "image/jpeg"; // Extend for webp if needed
        string b64 = Convert.ToBase64String(bytes);
        return $"data:{mime};base64,{b64}";
    }
}
