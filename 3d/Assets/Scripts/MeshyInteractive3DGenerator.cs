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
    [Tooltip("Single image: 1 image -> 3D model")]
    public Button pickAndGenerateButton;          // 单图按钮
    [Tooltip("Multi image: 1~4 images -> 3D model")]
    public Button pickAndGenerateMultiButton;     // 多图按钮
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
    [Tooltip("Load last saved model on app start (if any).")]
    public bool loadLastOnStart = true;

    [Header("Debug")]
    public bool autoOpenPickerOnStart = false; // Auto-open file picker on start (device only)

    // ==== Meshy OpenAPI ====
    const string BaseUrl = "https://api.meshy.ai/openapi/v1";

    // Local GLB storage
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

    // Multi-image create request body
    [Serializable]
    class MultiImageCreateReq
    {
        public string[] image_urls;   // 1~4 data URIs
        public string ai_model;
        public string topology;
        public int target_polycount;
        public string symmetry_mode;
        public bool should_remesh;
        public bool should_texture;
        public bool enable_pbr;
        public bool is_a_t_pose;
        public string texture_prompt;
        public string texture_image_url;
    }
    // Retexture request body
    [Serializable]
    class RetextureCreateReq
    {
        public string input_task_id;
        public string ai_model;
        public string text_style_prompt;
        public string image_style_url;
        public bool enable_original_uv;
        public bool enable_pbr;
    }

    int _nextSavedModelIndex = 0;
    bool _isGenerating = false;

    // 记录最后一次生成的根节点，用于贴图完成后替换
    GameObject _lastSpawnRoot;

    void Awake()
    {
        if (pickAndGenerateButton != null)
            pickAndGenerateButton.onClick.AddListener(OnPickButton);

        if (pickAndGenerateMultiButton != null)
            pickAndGenerateMultiButton.onClick.AddListener(OnPickMultiButton);
    }

    public void OnLoadAllButton()
    {
        StartCoroutine(CoLoadNextSavedModel());
    }

    // Switch glTFast shaders to URP Lit / Unlit (if you use URP in the future)
    void ConvertGlTFastMaterialsToURP(GameObject root)
    {
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

                if (shaderName.Contains("glTF"))
                {
                    bool isUnlit =
                        shaderName.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0;

                    Shader target = (isUnlit && unlitShader != null) ? unlitShader : litShader;
                    m.shader = target;
                }
            }
        }
    }

    void DumpMaterials(GameObject root)
    {
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
        // Load last saved model on startup
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

    // ================== Single image: geometry -> grey model -> retexture ==================

    public void OnPickButton()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_isGenerating)
        {
            Log("A generation task is already running. Please wait until it finishes.");
            return;
        }

        if (NativeFilePicker.IsFilePickerBusy()) return;

        NativeFilePicker.PickFile(OnFilePicked, new string[] { "image/*" });
#else
        Log("Please test on a Quest/Android device (system file picker is not available in the Editor).");
#endif
    }

    void OnFilePicked(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Log("Selection cancelled.");
            return;
        }

        if (_isGenerating)
        {
            Log("A generation task is already running. Please wait until it finishes.");
            return;
        }

        StartCoroutine(PipelineSingleGreyThenTexture(path));
    }

    IEnumerator PipelineSingleGreyThenTexture(string imagePath)
    {
        _isGenerating = true;

        Log("Reading image...");
        byte[] bytes = null;
        try
        {
            bytes = File.ReadAllBytes(imagePath);
        }
        catch (Exception e)
        {
            Log("Read failed: " + e.Message);
            _isGenerating = false;
            yield break;
        }

        string dataUri = ToDataUri(bytes, imagePath);

        // Step 1: image-to-3d geometry only (no texture)
        Log("Creating Meshy job (single image, geometry only)...");
        string geomTaskId = null;
        yield return CreateImageTo3DGeometryTask(
            dataUri,
            id => geomTaskId = id,
            err => Log(err)
        );

        if (string.IsNullOrEmpty(geomTaskId))
        {
            _isGenerating = false;
            yield break;
        }

        Log($"Geometry job created: {geomTaskId}. Polling base mesh...");
        string glbUrl = null;
        yield return PollUntilReadyImageTo3D(
            geomTaskId,
            (status, progress, url) =>
            {
                Log($"[Geometry] Status: {status} Progress: {progress}%");
                if (!string.IsNullOrEmpty(url)) glbUrl = url;
            },
            err => Log(err),
            3f
        );

        if (string.IsNullOrEmpty(glbUrl))
        {
            Log("No GLB download URL returned for geometry.");
            _isGenerating = false;
            yield break;
        }

        // Load grey model
        Log("Loading base mesh (grey model)...");
        var t = LoadGlbFromUrl(glbUrl);
        while (!t.IsCompleted) yield return null;

        if (t.IsFaulted)
        {
            Log("Load base mesh failed: " + t.Exception);
            Debug.LogException(t.Exception);
            _isGenerating = false;
            yield break;
        }

        var rawModel = t.Result;

        // Turn into grey model (remove textures, set grey color)
        MakeModelGrey(rawModel);

        SetupSpawnedModel(rawModel);
        GameObject greyRoot = _lastSpawnRoot;
        Log("Base mesh ready. Starting texturing in background...");

        // Step 2: retexture based on same task id (same geometry, new textures)
        string retexTaskId = null;
        yield return CreateRetextureTaskFromTaskId(
            geomTaskId,
            "photo-realistic, high detail, keep original base colors and materials, no extra decorations, no logos, no text, no symbols, clean surface, game-ready PBR textures",
            id => retexTaskId = id,
            err => Log(err)
        );

        if (string.IsNullOrEmpty(retexTaskId))
        {
            Log("Retexture creation failed. Keeping base mesh only.");
            _isGenerating = false;
            yield break;
        }

        Log($"Retexture task created: {retexTaskId}. Polling textured model...");

        string texturedGlbUrl = null;
        yield return PollUntilReadyRetexture(
            retexTaskId,
            (status, progress, url) =>
            {
                Log($"[Retexture] Status: {status} Progress: {progress}%");
                if (!string.IsNullOrEmpty(url)) texturedGlbUrl = url;
            },
            err => Log(err),
            3f
        );

        if (string.IsNullOrEmpty(texturedGlbUrl))
        {
            Log("No GLB download URL returned for retexture. Keeping base mesh only.");
            _isGenerating = false;
            yield break;
        }

        // Save final textured version
        StartCoroutine(SaveGlbAndRegister(texturedGlbUrl));

        Log("Loading textured model from URL...");
        var t2 = LoadGlbFromUrl(texturedGlbUrl);
        while (!t2.IsCompleted) yield return null;

        if (t2.IsFaulted)
        {
            Log("Load textured model failed: " + t2.Exception);
            Debug.LogException(t2.Exception);
            _isGenerating = false;
            yield break;
        }

        var texturedModel = t2.Result;

        // Replace grey model with textured one
        ReplaceLastSpawnedModel(texturedModel, greyRoot);

        Log("Done! Grey model has been replaced by textured model.");
        _isGenerating = false;
    }

    // Single-image: geometry-only image-to-3d
    IEnumerator CreateImageTo3DGeometryTask(string dataUri, Action<string> onOk, Action<string> onErr)
    {
        // Higher polycount for more detailed mesh (you can tweak between 40000~120000)
        int targetPoly = 80000;

        string json =
            "{" +
            $"\"image_url\":\"{dataUri}\"," +
            "\"ai_model\":\"meshy-5\"," +          // geometry 仍然用 meshy-5（保证和 retexture 兼容）
            "\"topology\":\"triangle\"," +
            $"\"target_polycount\":{targetPoly}," +
            "\"symmetry_mode\":\"off\"," +        // 关闭对称，避免奇怪对称 artifacts
            "\"should_remesh\":true," +           // 启用 remesh，遵守 target_polycount
            "\"should_texture\":false," +         // 只要几何，不要纹理
            "\"enable_pbr\":false," +
            "\"is_a_t_pose\":false" +
            "}";

        Debug.Log("[Meshy] ImageTo3D geometry request json: " + json);

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
                onErr?.Invoke($"Create image-to-3d (geometry) failed: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            var resp = JsonUtility.FromJson<CreateResp>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(resp?.result))
            {
                onErr?.Invoke("Create image-to-3d succeeded but no taskId returned.");
                yield break;
            }
            onOk?.Invoke(resp.result);
        }
    }


    IEnumerator PollUntilReadyImageTo3D(
        string taskId,
        Action<string, int, string> onProgress,
        Action<string> onErr,
        float intervalSec
    )
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
                    onErr?.Invoke($"Poll image-to-3d failed: {req.responseCode} {req.error}");
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

    // ================== Multi-image: geometry -> grey model -> retexture ==================

    public void OnPickMultiButton()
    {
        Debug.Log("[MeshyMulti] OnPickMultiButton CLICKED");

        if (Application.platform != RuntimePlatform.Android)
        {
            Log("Multi-image feature should be tested on a Quest/Android device (current platform: " + Application.platform + ").");
            return;
        }

        if (_isGenerating)
        {
            Log("A generation task is already running. Please wait until it finishes.");
            return;
        }

        if (NativeFilePicker.IsFilePickerBusy())
        {
            Debug.Log("[MeshyMulti] File picker is busy");
            return;
        }

        if (NativeFilePicker.CanPickMultipleFiles())
        {
            NativeFilePicker.PickMultipleFiles(OnFilesPickedMulti, new string[] { "image/*" });
        }
        else
        {
            NativeFilePicker.PickFile(path =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    Log("Selection cancelled.");
                    return;
                }

                if (_isGenerating)
                {
                    Log("A generation task is already running. Please wait until it finishes.");
                    return;
                }

                StartCoroutine(PipelineMultiGreyThenTexture(new List<string> { path }));
            }, new string[] { "image/*" });
        }
    }

    void OnFilesPickedMulti(string[] paths)
    {
        if (paths == null || paths.Length == 0)
        {
            Log("Selection cancelled.");
            return;
        }

        if (_isGenerating)
        {
            Log("A generation task is already running. Please wait until it finishes.");
            return;
        }

        var list = new List<string>();
        foreach (var p in paths)
        {
            if (!string.IsNullOrEmpty(p))
                list.Add(p);
        }

        if (list.Count == 0)
        {
            Log("No valid files selected.");
            return;
        }

        if (list.Count > 4)
            list = list.GetRange(0, 4);

        StartCoroutine(PipelineMultiGreyThenTexture(list));
    }

    IEnumerator PipelineMultiGreyThenTexture(List<string> imagePaths)
    {
        _isGenerating = true;

        if (imagePaths == null || imagePaths.Count == 0)
        {
            Log("No images selected.");
            _isGenerating = false;
            yield break;
        }

        Log("Reading images...");
        var dataUris = new List<string>();

        foreach (var imagePath in imagePaths)
        {
            if (string.IsNullOrEmpty(imagePath)) continue;

            byte[] bytes = null;
            try
            {
                bytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception e)
            {
                Log("Read failed: " + e.Message);
                _isGenerating = false;
                yield break;
            }

            string dataUri = ToDataUri(bytes, imagePath);
            dataUris.Add(dataUri);
        }

        if (dataUris.Count == 0)
        {
            Log("No valid images read.");
            _isGenerating = false;
            yield break;
        }

        // Step 1: multi-image geometry only
        Log("Creating Meshy job (multi-image, geometry only)...");
        string geomTaskId = null;
        yield return CreateMultiImageTo3DGeometryTask(
            dataUris,
            id => geomTaskId = id,
            err => Log(err)
        );

        if (string.IsNullOrEmpty(geomTaskId))
        {
            _isGenerating = false;
            yield break;
        }

        Log($"Multi-image geometry job created: {geomTaskId}. Polling base mesh...");
        string glbUrl = null;
        yield return PollUntilReadyMultiImageTo3D(
            geomTaskId,
            (status, progress, url) =>
            {
                Log($"[Multi Geometry] Status: {status} Progress: {progress}%");
                if (!string.IsNullOrEmpty(url)) glbUrl = url;
            },
            err => Log(err),
            3f
        );

        if (string.IsNullOrEmpty(glbUrl))
        {
            Log("No GLB download URL returned for multi-image geometry.");
            _isGenerating = false;
            yield break;
        }

        Log("Loading multi-image base mesh (grey model)...");
        var t = LoadGlbFromUrl(glbUrl);
        while (!t.IsCompleted) yield return null;

        if (t.IsFaulted)
        {
            Log("Load multi-image base mesh failed: " + t.Exception);
            Debug.LogException(t.Exception);
            _isGenerating = false;
            yield break;
        }

        var rawModel = t.Result;
        MakeModelGrey(rawModel);
        SetupSpawnedModel(rawModel);
        GameObject greyRoot = _lastSpawnRoot;
        Log("Multi-image base mesh ready. Starting texturing in background...");

        // Step 2: retexture
        // Note: official docs do not explicitly say multi-image tasks are valid input_task_id for retexture.
        // This may need to be verified in practice.
        string retexTaskId = null;
        yield return CreateRetextureTaskFromTaskId(
            geomTaskId,
            "photo-realistic, high detail, keep original base colors and materials, no extra decorations, no logos, no text, no symbols, clean surface, game-ready PBR textures",
            id => retexTaskId = id,
            err => Log(err)
        );

        if (string.IsNullOrEmpty(retexTaskId))
        {
            Log("Retexture creation failed for multi-image. Keeping base mesh only.");
            _isGenerating = false;
            yield break;
        }

        Log($"Multi-image retexture task created: {retexTaskId}. Polling textured model...");

        string texturedGlbUrl = null;
        yield return PollUntilReadyRetexture(
            retexTaskId,
            (status, progress, url) =>
            {
                Log($"[Multi Retexture] Status: {status} Progress: {progress}%");
                if (!string.IsNullOrEmpty(url)) texturedGlbUrl = url;
            },
            err => Log(err),
            3f
        );

        if (string.IsNullOrEmpty(texturedGlbUrl))
        {
            Log("No GLB download URL returned for multi-image retexture. Keeping base mesh only.");
            _isGenerating = false;
            yield break;
        }

        StartCoroutine(SaveGlbAndRegister(texturedGlbUrl));

        Log("Loading multi-image textured model from URL...");
        var t2 = LoadGlbFromUrl(texturedGlbUrl);
        while (!t2.IsCompleted) yield return null;

        if (t2.IsFaulted)
        {
            Log("Load multi-image textured model failed: " + t2.Exception);
            Debug.LogException(t2.Exception);
            _isGenerating = false;
            yield break;
        }

        var texturedModel = t2.Result;
        ReplaceLastSpawnedModel(texturedModel, greyRoot);

        Log("Done! Multi-image grey model has been replaced by textured model.");

        _isGenerating = false;
    }

    IEnumerator CreateMultiImageTo3DGeometryTask(
      List<string> dataUris,
      Action<string> onOk,
      Action<string> onErr
  )
    {
        if (dataUris == null || dataUris.Count == 0)
        {
            onErr?.Invoke("No images provided for multi-image task.");
            yield break;
        }

        if (dataUris.Count > 4)
            dataUris = dataUris.GetRange(0, 4);

        // Higher polycount for more detailed mesh
        int targetPoly = 80000;

        var payload = new MultiImageCreateReq
        {
            image_urls = dataUris.ToArray(),
            ai_model = "meshy-5",           // geometry 仍然用 meshy-5
            topology = "triangle",
            target_polycount = targetPoly,
            symmetry_mode = "off",          // 关闭对称
            should_remesh = true,           // 按 target_polycount remesh
            should_texture = false,         // 只要几何
            enable_pbr = false,
            is_a_t_pose = false,
            texture_prompt = null,
            texture_image_url = null
        };

        string json = JsonUtility.ToJson(payload);
        Debug.Log("[Meshy] Multi-image geometry request json: " + json);

        byte[] body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest($"{BaseUrl}/multi-image-to-3d", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onErr?.Invoke($"Create multi-image geometry failed: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            var resp = JsonUtility.FromJson<CreateResp>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(resp?.result))
            {
                onErr?.Invoke("Create multi-image geometry succeeded but no taskId returned.");
                yield break;
            }

            onOk?.Invoke(resp.result);
        }
    }


    IEnumerator PollUntilReadyMultiImageTo3D(
        string taskId,
        Action<string, int, string> onProgress,
        Action<string> onErr,
        float intervalSec
    )
    {
        while (true)
        {
            using (var req = UnityWebRequest.Get($"{BaseUrl}/multi-image-to-3d/{taskId}"))
            {
                req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
                req.downloadHandler = new DownloadHandlerBuffer();
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"Poll multi-image-to-3d failed: {req.responseCode} {req.error}");
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

    // ================== Retexture (for both single & multi) ==================

    IEnumerator CreateRetextureTaskFromTaskId(
       string inputTaskId,
       string textStylePrompt,
       Action<string> onOk,
       Action<string> onErr
   )
    {
        var payload = new RetextureCreateReq
        {
            input_task_id = inputTaskId,
            // Use the most advanced model for texturing (Meshy 6 Preview)
            ai_model = "latest",
            text_style_prompt = textStylePrompt,
            image_style_url = null,
            enable_original_uv = true,
            enable_pbr = true
        };

        string json = JsonUtility.ToJson(payload);
        Debug.Log("[Meshy] Retexture request json: " + json);

        byte[] body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest($"{BaseUrl}/retexture", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onErr?.Invoke($"Create retexture failed: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            var resp = JsonUtility.FromJson<CreateResp>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(resp?.result))
            {
                onErr?.Invoke("Create retexture succeeded but no taskId returned.");
                yield break;
            }

            onOk?.Invoke(resp.result);
        }
    }


    IEnumerator PollUntilReadyRetexture(
        string taskId,
        Action<string, int, string> onProgress,
        Action<string> onErr,
        float intervalSec
    )
    {
        while (true)
        {
            using (var req = UnityWebRequest.Get($"{BaseUrl}/retexture/{taskId}"))
            {
                req.SetRequestHeader("Authorization", $"Bearer {meshyApiKey}");
                req.downloadHandler = new DownloadHandlerBuffer();
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onErr?.Invoke($"Poll retexture failed: {req.responseCode} {req.error}");
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

    // ============ Save multiple GLBs ============

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

            string fileName = $"meshy_{DateTime.Now:yyyyMMdd_HHmmss}.glb";
            string filePath = Path.Combine(dir, fileName);
            File.WriteAllBytes(filePath, data);

            var list = LoadModelList();
            list.files.Add(filePath);
            SaveModelList(list);

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

    // ============ Load saved models one by one (each click loads one) ============

    public void LoadAllSavedModels()
    {
        StartCoroutine(CoLoadNextSavedModel());
    }

    IEnumerator CoLoadNextSavedModel()
    {
        var list = LoadModelList();
        if (list.files == null || list.files.Count == 0)
        {
            Log("No saved models found.");
            yield break;
        }

        if (_nextSavedModelIndex >= list.files.Count)
            _nextSavedModelIndex = 0;

        string path = list.files[_nextSavedModelIndex];
        int humanIndex = _nextSavedModelIndex + 1;
        _nextSavedModelIndex++;

        if (!File.Exists(path))
        {
            Log("Saved model not found: " + path);
            yield break;
        }

        Log($"Loading saved model {humanIndex}/{list.files.Count} ...");

        var t = LoadGlbFromFile(path);
        while (!t.IsCompleted) yield return null;

        if (!t.IsFaulted)
        {
            var rawModel = t.Result;
            SetupSpawnedModel(rawModel);
            Log($"Saved model {humanIndex}/{list.files.Count} loaded.");
        }
        else
        {
            Log("Failed to load saved model: " + t.Exception);
        }
    }

    // ============ Grey model helper + replace helper ============

    void MakeModelGrey(GameObject root)
    {
        var rends = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in rends)
        {
            var mats = r.materials; // instance materials
            foreach (var m in mats)
            {
                if (m == null) continue;

                if (m.HasProperty("_BaseMap"))
                    m.SetTexture("_BaseMap", null);
                if (m.HasProperty("_MainTex"))
                    m.SetTexture("_MainTex", null);

                if (m.HasProperty("_BaseColor"))
                    m.SetColor("_BaseColor", Color.gray);
                else if (m.HasProperty("_Color"))
                    m.SetColor("_Color", Color.gray);
            }
        }
    }

    // New overload: explicitly say which root to destroy
    void ReplaceLastSpawnedModel(GameObject newRawModel, GameObject oldRoot)
    {
        // Spawn the new model (this will update _lastSpawnRoot)
        SetupSpawnedModel(newRawModel);

        // Destroy the specific old root we passed in
        if (oldRoot != null)
        {
            Destroy(oldRoot);
        }
    }

    // Backward-compatible overload (if you still want to use it somewhere)
    void ReplaceLastSpawnedModel(GameObject newRawModel)
    {
        ReplaceLastSpawnedModel(newRawModel, _lastSpawnRoot);
    }


    // 统一处理：归一化尺寸 + HandGrab 包装 + 放到玩家前方 + 隐藏壳子的可见方块
    void SetupSpawnedModel(GameObject rawModel)
    {
        Debug.Log("[MeshyTest] SetupSpawnedModel CALLED");

        var b0 = ComputeWorldBounds(rawModel);
        Debug.Log($"[MeshyInteractive3DGenerator] Bounds before normalize: {b0.size}");

        NormalizeBounds(rawModel, targetSizeMeters);

        var b1 = ComputeWorldBounds(rawModel);
        Debug.Log($"[MeshyInteractive3DGenerator] Bounds after normalize: {b1.size}");

        GameObject rootForState = null;

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

            rootForState = wrapper;
        }
        else
        {
            PlaceObject(rawModel.transform);
            EnsureBoundsCollider(rawModel.gameObject);

            rootForState = rawModel;
        }

        _lastSpawnRoot = rootForState;

        DumpMaterials(rawModel);
    }

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
