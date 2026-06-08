using System.Collections;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine.SceneManagement;
using UnityExplorer.ObjectExplorer;

namespace UnityExplorer.McpBridge
{
    internal static class McpBridgeServer
    {
        private const int DefaultPort = 8765;
        private const int MaxLimit = 100;
        private const string TokenHeader = "X-UnityExplorer-MCP-Token";

        private static readonly object QueueLock = new();
        private static readonly Queue<BridgeWorkItem> WorkQueue = new();
        private static readonly Dictionary<string, object> Handles = new();
        private static readonly Dictionary<object, string> ReverseHandles = new(new ReferenceEqualityComparer());
        private static readonly Dictionary<string, MemberRecord> Members = new();
        private static readonly Dictionary<string, PendingAction> PendingActions = new();

        private static HttpListener listener;
        private static Thread listenerThread;
        private static bool running;
        private static string token;
        private static int port;

        public static bool IsRunning => running;
        public static string Url => running ? $"http://127.0.0.1:{port}/" : null;

        public static void Start()
        {
            if (running)
                return;

            try
            {
                token = Guid.NewGuid().ToString("N");
                listener = CreateListener();
                listener.Start();

                running = true;
                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "UnityExplorer MCP Bridge"
                };
                listenerThread.Start();

                WriteConnectionFile();
                ExplorerCore.Log($"UnityExplorer MCP bridge listening on {Url}");
            }
            catch (Exception ex)
            {
                running = false;
                ExplorerCore.LogWarning($"Unable to start UnityExplorer MCP bridge: {ex.ReflectionExToString()}");
            }
        }

        public static void Stop()
        {
            running = false;

            try { listener?.Stop(); }
            catch { }

            try { listener?.Close(); }
            catch { }

            listener = null;
        }

        public static void ProcessMainThreadQueue()
        {
            while (true)
            {
                BridgeWorkItem item = null;

                lock (QueueLock)
                {
                    if (WorkQueue.Count > 0)
                        item = WorkQueue.Dequeue();
                }

                if (item == null)
                    break;

                try
                {
                    item.Result = Dispatch(item.Method, item.Params);
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }
        }

        private static HttpListener CreateListener()
        {
            Exception lastError = null;
            for (int i = 0; i < 20; i++)
            {
                int candidate = DefaultPort + i;
                HttpListener http = new();
                http.Prefixes.Add($"http://127.0.0.1:{candidate}/");

                try
                {
                    http.Start();
                    http.Stop();
                    port = candidate;
                    return http;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { http.Close(); } catch { }
                }
            }

            throw new InvalidOperationException("No local bridge port was available.", lastError);
        }

        private static void ListenLoop()
        {
            while (running && listener != null)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleContext(context));
                }
                catch
                {
                    if (running)
                        Thread.Sleep(100);
                }
            }
        }

        private static void HandleContext(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath.Trim('/') != "rpc")
                {
                    WriteResponse(context, 404, Error("not_found", "Use POST /rpc."));
                    return;
                }

                if (context.Request.Headers[TokenHeader] != token)
                {
                    WriteResponse(context, 401, Error("unauthorized", "Missing or invalid bridge token."));
                    return;
                }

                string body;
                using (StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                Dictionary<string, object> request = AsDict(MiniJson.Deserialize(body));
                string method = GetString(request, "method");
                Dictionary<string, object> args = AsDict(GetValue(request, "params"));

                BridgeWorkItem item = new(method, args);
                lock (QueueLock)
                    WorkQueue.Enqueue(item);

                if (!item.Done.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    WriteResponse(context, 504, Error("timeout", "Unity main thread did not answer within 30 seconds."));
                    return;
                }

                if (item.Error != null)
                {
                    WriteResponse(context, 500, Error("bridge_error", item.Error.ReflectionExToString()));
                    return;
                }

                WriteResponse(context, 200, Ok(item.Result));
            }
            catch (Exception ex)
            {
                try
                {
                    WriteResponse(context, 500, Error("bridge_error", ex.ReflectionExToString()));
                }
                catch { }
            }
        }

        private static object Dispatch(string method, Dictionary<string, object> args)
        {
            return method switch
            {
                "status" => Status(),
                "list_scenes" => ListScenes(),
                "search_objects" => SearchObjects(args),
                "get_object" => GetObject(args),
                "list_hierarchy" => ListHierarchy(args),
                "list_components" => ListComponents(args),
                "list_members" => ListMembers(args),
                "read_member" => ReadMember(args),
                "write_member_preview" => WriteMemberPreview(args),
                "apply_confirmed_write" => ApplyPending(args, "write"),
                "invoke_member_preview" => InvokeMemberPreview(args),
                "apply_confirmed_invoke" => ApplyPending(args, "invoke"),
                "generate_mod_recipe" => GenerateModRecipe(args),
                _ => throw new InvalidOperationException($"Unknown bridge method '{method}'.")
            };
        }

        private static Dictionary<string, object> Status()
        {
            return Dict(
                "bridge", "UnityExplorer MCP Bridge",
                "running", true,
                "url", Url,
                "unityExplorerVersion", ExplorerCore.VERSION,
                "unityProduct", Application.productName,
                "unityVersion", Application.unityVersion,
                "platform", Application.platform.ToString(),
                "loaderFolder", ExplorerCore.ExplorerFolder,
                "supportBaseline", "BepInEx 5 Mono"
            );
        }

        private static Dictionary<string, object> ListScenes()
        {
            SceneHandler.Update();
            List<object> scenes = new();

            foreach (Scene scene in SceneHandler.LoadedScenes)
            {
                scenes.Add(Dict(
                    "handle", GetHandle(scene),
                    "name", GetSceneName(scene),
                    "path", scene.path,
                    "buildIndex", scene.buildIndex,
                    "handleId", scene.handle,
                    "isLoaded", scene.isLoaded,
                    "isValid", scene.IsValid(),
                    "rootCount", scene.IsValid() ? RuntimeHelper.GetRootGameObjects(scene).Count() : 0
                ));
            }

            return Dict("items", scenes, "count", scenes.Count);
        }

        private static Dictionary<string, object> SearchObjects(Dictionary<string, object> args)
        {
            string sceneFilter = GetString(args, "scene");
            string nameOrPath = GetString(args, "nameOrPathContains");
            string textContains = GetString(args, "textContains");
            List<string> componentTypes = GetStringList(GetValue(args, "componentTypesAny"));
            int limit = GetLimit(args);
            int cursor = GetCursor(args);

            if (string.IsNullOrEmpty(sceneFilter)
                && string.IsNullOrEmpty(nameOrPath)
                && string.IsNullOrEmpty(textContains)
                && !componentTypes.Any())
            {
                return Dict(
                    "items", new List<object>(),
                    "count", 0,
                    "nextCursor", null,
                    "warning", "Refusing unfiltered whole-scene search. Provide scene, nameOrPathContains, textContains, or componentTypesAny."
                );
            }

            UnityEngine.Object[] allObjects = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
            List<GameObject> matches = new();

            foreach (UnityEngine.Object obj in allObjects)
            {
                GameObject go = obj.TryCast<GameObject>();
                if (!go || IsUnityExplorerObject(go))
                    continue;

                if (!MatchesScene(go, sceneFilter))
                    continue;

                List<string> evidence = new();
                if (!string.IsNullOrEmpty(nameOrPath))
                {
                    string path = SafePath(go);
                    if (!go.name.ContainsIgnoreCase(nameOrPath) && !path.ContainsIgnoreCase(nameOrPath))
                        continue;
                    evidence.Add($"name/path contains '{nameOrPath}'");
                }

                if (componentTypes.Any())
                {
                    List<string> actualComponents = GetComponentTypeNames(go);
                    bool any = componentTypes.Any(filter => actualComponents.Any(actual => actual.ContainsIgnoreCase(filter)));
                    if (!any)
                        continue;
                    evidence.Add("component type matched");
                }

                if (!string.IsNullOrEmpty(textContains))
                {
                    string text = TryGetVisibleText(go);
                    bool matchedSelf = !string.IsNullOrEmpty(text) && text.ContainsIgnoreCase(textContains);
                    bool matchedChild = false;
                    if (!matchedSelf)
                    {
                        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                        {
                            if (child == go.transform)
                                continue;
                            string childText = TryGetVisibleText(child.gameObject);
                            if (!string.IsNullOrEmpty(childText) && childText.ContainsIgnoreCase(textContains))
                            {
                                matchedChild = true;
                                break;
                            }
                        }
                    }

                    if (!matchedSelf && !matchedChild)
                        continue;

                    evidence.Add(matchedSelf ? $"visible text contains '{textContains}'" : $"child visible text contains '{textContains}'");
                }

                matches.Add(go);
            }

            List<object> page = Page(matches.OrderBy(go => SafePath(go)).ToList(), cursor, limit, go => Summary(go));
            return Dict(
                "items", page,
                "count", matches.Count,
                "nextCursor", cursor + limit < matches.Count ? (object)(cursor + limit).ToString(CultureInfo.InvariantCulture) : null
            );
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> args)
        {
            GameObject go = Resolve<GameObject>(GetString(args, "handle"));
            return Summary(go);
        }

        private static Dictionary<string, object> ListHierarchy(Dictionary<string, object> args)
        {
            string rootHandle = GetString(args, "rootHandle");
            int depth = Math.Max(0, Math.Min(GetInt(args, "depth", 1), 8));
            string textContains = GetString(args, "textContains");
            string nameOrPath = GetString(args, "nameOrPathContains");
            int limit = GetLimit(args);
            int cursor = GetCursor(args);

            List<GameObject> candidates = new();
            if (!string.IsNullOrEmpty(rootHandle))
            {
                GameObject root = Resolve<GameObject>(rootHandle);
                CollectHierarchy(root.transform, depth, candidates);
            }
            else
            {
                SceneHandler.Update();
                foreach (Scene scene in SceneHandler.LoadedScenes)
                {
                    if (!scene.IsValid())
                        continue;
                    foreach (GameObject root in RuntimeHelper.GetRootGameObjects(scene))
                        CollectHierarchy(root.transform, depth, candidates);
                }
            }

            List<GameObject> matches = new();
            foreach (GameObject go in candidates)
            {
                if (IsUnityExplorerObject(go))
                    continue;

                if (!string.IsNullOrEmpty(nameOrPath)
                    && !go.name.ContainsIgnoreCase(nameOrPath)
                    && !SafePath(go).ContainsIgnoreCase(nameOrPath))
                    continue;

                if (!string.IsNullOrEmpty(textContains))
                {
                    string text = TryGetVisibleText(go);
                    if (string.IsNullOrEmpty(text) || !text.ContainsIgnoreCase(textContains))
                        continue;
                }

                matches.Add(go);
            }

            return Dict(
                "items", Page(matches, cursor, limit, go => Summary(go)),
                "count", matches.Count,
                "nextCursor", cursor + limit < matches.Count ? (object)(cursor + limit).ToString(CultureInfo.InvariantCulture) : null
            );
        }

        private static Dictionary<string, object> ListComponents(Dictionary<string, object> args)
        {
            GameObject go = Resolve<GameObject>(GetString(args, "objectHandle"));
            List<object> items = new();
            Component[] components = go.GetComponents<Component>();

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (!component)
                    continue;

                Type type = component.GetActualType();
                items.Add(Dict(
                    "handle", GetHandle(component),
                    "kind", "Component",
                    "index", i,
                    "type", TypeName(type),
                    "fullType", type.FullName,
                    "assembly", type.Assembly.GetName().Name,
                    "enabled", component is Behaviour behaviour ? (object)behaviour.enabled : null,
                    "gameObjectHandle", GetHandle(go),
                    "gameObjectPath", SafePath(go),
                    "summary", ToStringUtility.ToStringWithType(component, type, true)
                ));
            }

            return Dict("items", items, "count", items.Count);
        }

        private static Dictionary<string, object> ListMembers(Dictionary<string, object> args)
        {
            object target = ResolveAny(GetString(args, "targetHandle"));
            Type targetType = target as Type ?? target.GetActualType();
            string filter = GetString(args, "filter");
            HashSet<string> kinds = new(GetStringList(GetValue(args, "kinds")), StringComparer.OrdinalIgnoreCase);
            bool includeValues = GetBool(args, "includeValues", false);
            int limit = GetLimit(args);
            int cursor = GetCursor(args);

            if (!kinds.Any())
            {
                kinds.Add("field");
                kinds.Add("property");
                kinds.Add("method");
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            List<MemberInfo> members = new();
            foreach (Type type in ReflectionUtility.GetAllBaseTypes(targetType))
            {
                if (kinds.Contains("field"))
                    members.AddRange(type.GetFields(flags).Where(member => member.DeclaringType == type).Cast<MemberInfo>());
                if (kinds.Contains("property"))
                    members.AddRange(type.GetProperties(flags).Where(member => member.DeclaringType == type).Cast<MemberInfo>());
                if (kinds.Contains("method"))
                    members.AddRange(type.GetMethods(flags).Where(member => member.DeclaringType == type && !member.Name.StartsWith("get_") && !member.Name.StartsWith("set_")).Cast<MemberInfo>());
            }

            List<MemberInfo> filtered = members
                .Where(member => string.IsNullOrEmpty(filter) || member.Name.ContainsIgnoreCase(filter))
                .OrderBy(member => member.MemberType.ToString())
                .ThenBy(member => member.Name)
                .ToList();

            List<object> page = Page(filtered, cursor, limit, member => MemberSummary(target, member, includeValues));
            return Dict(
                "items", page,
                "count", filtered.Count,
                "nextCursor", cursor + limit < filtered.Count ? (object)(cursor + limit).ToString(CultureInfo.InvariantCulture) : null
            );
        }

        private static Dictionary<string, object> ReadMember(Dictionary<string, object> args)
        {
            MemberRecord record = ResolveMember(args);
            object value = ReadMemberValue(record);
            return Dict(
                "member", MemberSummary(record.Target, record.Member, false),
                "value", ValueSummary(value, GetMemberValueType(record.Member)),
                "risk", record.Member is PropertyInfo ? "property-getter" : "field-read"
            );
        }

        private static Dictionary<string, object> WriteMemberPreview(Dictionary<string, object> args)
        {
            MemberRecord record = ResolveMember(args);
            object rawValue = GetValue(args, "value");
            Type valueType = GetMemberValueType(record.Member);
            if (valueType == null)
                throw new InvalidOperationException("Only fields and non-indexed properties can be written.");

            object parsed = ParseInput(rawValue, valueType);
            string actionToken = Guid.NewGuid().ToString("N");
            PendingActions[actionToken] = new PendingAction
            {
                Kind = "write",
                Description = $"Write {record.Member.Name} on {TypeName(record.Target as Type ?? record.Target.GetActualType())}",
                Apply = () =>
                {
                    WriteMemberValue(record, parsed);
                    return Dict("applied", true, "value", ValueSummary(ReadMemberValue(record), valueType));
                }
            };

            return Dict(
                "requiresConfirmation", true,
                "actionToken", actionToken,
                "member", MemberSummary(record.Target, record.Member, false),
                "parsedValue", ValueSummary(parsed, valueType),
                "risk", "runtime mutation; does not persist to mod source"
            );
        }

        private static Dictionary<string, object> InvokeMemberPreview(Dictionary<string, object> args)
        {
            MemberRecord record = ResolveMember(args);
            if (record.Member is not MethodInfo method)
                throw new InvalidOperationException("Only methods can be invoked.");

            ParameterInfo[] parameters = method.GetParameters();
            List<object> rawArgs = GetList(GetValue(args, "arguments"));
            if (parameters.Length != rawArgs.Count)
                throw new InvalidOperationException($"Method expects {parameters.Length} arguments, received {rawArgs.Count}.");

            object[] parsedArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                parsedArgs[i] = ParseInput(rawArgs[i], parameters[i].ParameterType);

            string actionToken = Guid.NewGuid().ToString("N");
            PendingActions[actionToken] = new PendingAction
            {
                Kind = "invoke",
                Description = $"Invoke {method.Name} on {TypeName(record.Target as Type ?? record.Target.GetActualType())}",
                Apply = () =>
                {
                    object result = method.Invoke(GetInstance(record), parsedArgs);
                    return Dict("applied", true, "result", ValueSummary(result, method.ReturnType));
                }
            };

            return Dict(
                "requiresConfirmation", true,
                "actionToken", actionToken,
                "member", MemberSummary(record.Target, record.Member, false),
                "arguments", parsedArgs.Select(arg => ValueSummary(arg, arg?.GetActualType())).Cast<object>().ToList(),
                "risk", "method invocation may have side effects"
            );
        }

        private static object ApplyPending(Dictionary<string, object> args, string expectedKind)
        {
            string actionToken = GetString(args, "actionToken");
            if (string.IsNullOrEmpty(actionToken) || !PendingActions.TryGetValue(actionToken, out PendingAction action))
                throw new InvalidOperationException("Unknown or expired confirmation token.");

            if (action.Kind != expectedKind)
                throw new InvalidOperationException($"Confirmation token is for '{action.Kind}', not '{expectedKind}'.");

            PendingActions.Remove(actionToken);
            return action.Apply();
        }

        private static Dictionary<string, object> GenerateModRecipe(Dictionary<string, object> args)
        {
            string framework = GetString(args, "framework") ?? "BepInExHarmony";
            string goal = GetString(args, "goal") ?? "Modify discovered Unity UI behavior";
            List<string> evidenceHandles = GetStringList(GetValue(args, "evidenceHandles"));
            List<string> evidence = new();

            foreach (string handle in evidenceHandles)
            {
                object target = ResolveAny(handle, false);
                if (target is GameObject go)
                    evidence.Add($"{handle}: GameObject {SafePath(go)} components=[{string.Join(", ", GetComponentTypeNames(go).ToArray())}] text='{TryGetVisibleText(go)}'");
                else if (target is Component component)
                    evidence.Add($"{handle}: Component {TypeName(component.GetActualType())} on {SafePath(component.gameObject)}");
                else if (target is Type type)
                    evidence.Add($"{handle}: Type {type.FullName}");
            }

            string recipe =
$@"# {goal}

Framework: {framework}

Discovery strategy:
- Wait for the target scene/settings UI to exist.
- Locate the settings content/root by stable path fragments and visible label text rather than clone index alone.
- Select the row whose child label text matches the target setting, then inspect sibling children for Slider and Value text objects.

Runtime patch strategy:
- For BepInEx 5 Mono, implement a BaseUnityPlugin and Harmony patch or coroutine that runs after the settings screen is created.
- Resolve the row each time the menu opens so regenerated UI clones are handled.
- Prefer adding a TMP_InputField or companion input behavior to the Value child; keep TextMeshProUGUI as display output.
- Parse user text with optional suffixes such as 'x', clamp to the discovered Slider.minValue/maxValue, assign Slider.value, then invoke the slider/controller update path.

Evidence:
{string.Join("\n", evidence.Select(line => "- " + line).ToArray())}

Safety notes:
- Do not hard-code sibling index unless label/component evidence also matches.
- Re-run discovery if the row or slider handle is destroyed.
- Keep the original slider event flow so game controller state remains authoritative.";

            return Dict("recipe", recipe, "evidenceCount", evidence.Count);
        }

        private static Dictionary<string, object> Summary(GameObject go)
        {
            List<string> evidence = new();
            string text = TryGetVisibleText(go);
            if (!string.IsNullOrEmpty(text))
                evidence.Add($"visible text == {text}");

            return Dict(
                "handle", GetHandle(go),
                "kind", "GameObject",
                "name", go.name,
                "path", SafePath(go),
                "scene", GetSceneName(go.scene),
                "activeSelf", go.activeSelf,
                "activeInHierarchy", go.activeInHierarchy,
                "layer", LayerMask.LayerToName(go.layer),
                "tag", SafeTag(go),
                "siblingIndex", go.transform.GetSiblingIndex(),
                "childCount", go.transform.childCount,
                "components", GetComponentTypeNames(go),
                "text", text,
                "evidence", evidence
            );
        }

        private static Dictionary<string, object> MemberSummary(object target, MemberInfo member, bool includeValue)
        {
            string handle = GetMemberHandle(target, member);
            Dictionary<string, object> dict = Dict(
                "handle", handle,
                "memberId", $"{member.MemberType.ToString().ToLowerInvariant()}:{member.Name}",
                "name", member.Name,
                "kind", member.MemberType.ToString(),
                "declaringType", member.DeclaringType.FullName,
                "isStatic", IsStatic(member),
                "canWrite", CanWrite(member),
                "valueType", GetMemberValueType(member)?.FullName,
                "hasArguments", HasArguments(member)
            );

            if (includeValue && member is FieldInfo or PropertyInfo)
            {
                try
                {
                    dict["value"] = ValueSummary(ReadMemberValue(new MemberRecord(target, member)), GetMemberValueType(member));
                }
                catch (Exception ex)
                {
                    dict["valueError"] = ex.GetType().Name + ": " + ex.Message;
                }
            }

            return dict;
        }

        private static Dictionary<string, object> ValueSummary(object value, Type fallbackType)
        {
            Type type = value == null || value.IsNullOrDestroyed() ? fallbackType : value.GetActualType();
            Dictionary<string, object> dict = Dict(
                "isNull", value == null || value.IsNullOrDestroyed(),
                "type", type?.FullName
            );

            if (value == null || value.IsNullOrDestroyed())
                return dict;

            if (value is string str)
            {
                dict["value"] = str.Length > 300 ? str.Substring(0, 300) : str;
                dict["length"] = str.Length;
            }
            else if (value is bool || value is int || value is long || value is float || value is double || value is decimal)
            {
                dict["value"] = value;
            }
            else if (value is UnityEngine.Object unityObject)
            {
                dict["handle"] = GetHandle(unityObject);
                dict["name"] = unityObject.name;
                if (unityObject is GameObject go)
                    dict["path"] = SafePath(go);
                else if (unityObject is Component component)
                    dict["gameObjectPath"] = SafePath(component.gameObject);
            }
            else if (value is ICollection collection)
            {
                dict["count"] = collection.Count;
            }
            else
            {
                dict["preview"] = ToStringUtility.ToStringWithType(value, fallbackType, true);
            }

            return dict;
        }

        private static object ReadMemberValue(MemberRecord record)
        {
            if (record.Member is FieldInfo field)
                return field.GetValue(GetInstance(record));

            if (record.Member is PropertyInfo property)
            {
                if (property.GetIndexParameters().Length > 0)
                    throw new InvalidOperationException("Indexed properties require invocation with arguments.");
                return property.GetValue(GetInstance(record), null);
            }

            throw new InvalidOperationException("Only fields and non-indexed properties can be read.");
        }

        private static void WriteMemberValue(MemberRecord record, object value)
        {
            if (record.Member is FieldInfo field)
            {
                field.SetValue(GetInstance(record), value);
                return;
            }

            if (record.Member is PropertyInfo property)
            {
                if (!property.CanWrite)
                    throw new InvalidOperationException("Property is not writable.");
                if (property.GetIndexParameters().Length > 0)
                    throw new InvalidOperationException("Indexed properties are not supported for writes.");
                property.SetValue(GetInstance(record), value, null);
                return;
            }

            throw new InvalidOperationException("Only fields and non-indexed properties can be written.");
        }

        private static object ParseInput(object rawValue, Type type)
        {
            if (type == typeof(string))
                return rawValue?.ToString();

            if (rawValue == null)
                return null;

            if (rawValue.GetActualType() == type || type.IsAssignableFrom(rawValue.GetActualType()))
                return rawValue;

            string text = rawValue.ToString();
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                text = text.Trim().TrimEnd('x', 'X');

            if (type == typeof(Type))
                return ReflectionUtility.GetTypeByName(text);

            if (ParseUtility.TryParse(text, type, out object parsed, out Exception ex))
                return parsed;

            throw new InvalidOperationException($"Cannot parse '{text}' as {type.FullName}: {ex?.Message}");
        }

        private static void CollectHierarchy(Transform transform, int depth, List<GameObject> results)
        {
            if (!transform)
                return;

            results.Add(transform.gameObject);
            if (depth <= 0)
                return;

            for (int i = 0; i < transform.childCount; i++)
                CollectHierarchy(transform.GetChild(i), depth - 1, results);
        }

        private static bool MatchesScene(GameObject go, string sceneFilter)
        {
            if (string.IsNullOrEmpty(sceneFilter))
                return true;

            string sceneName = GetSceneName(go.scene);
            return sceneName.ContainsIgnoreCase(sceneFilter) || go.scene.path.ContainsIgnoreCase(sceneFilter);
        }

        private static bool IsUnityExplorerObject(GameObject go)
            => go.transform.root && go.transform.root.name == "UniverseLibCanvas";

        private static string SafePath(GameObject go)
        {
            try { return go.transform.GetTransformPath(); }
            catch { return go.name; }
        }

        private static string SafeTag(GameObject go)
        {
            try { return go.tag; }
            catch { return ""; }
        }

        private static string TryGetVisibleText(GameObject go)
        {
            Text text = go.GetComponent<Text>();
            if (text)
                return text.text;

            foreach (Component component in go.GetComponents<Component>())
            {
                if (!component)
                    continue;
                Type type = component.GetActualType();
                if (type.FullName == "TMPro.TextMeshProUGUI" || type.FullName == "TMPro.TMP_Text" || type.FullName.Contains("TextMeshPro"))
                {
                    PropertyInfo prop = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        try { return prop.GetValue(component, null)?.ToString(); }
                        catch { }
                    }
                }
            }

            return null;
        }

        private static List<string> GetComponentTypeNames(GameObject go)
        {
            List<string> names = new();
            foreach (Component component in go.GetComponents<Component>())
            {
                if (!component)
                    continue;
                names.Add(TypeName(component.GetActualType()));
            }
            return names;
        }

        private static string GetSceneName(Scene scene)
        {
            if (!scene.IsValid())
                return "HideAndDontSave";
            if (scene.handle == -12)
                return "DontDestroyOnLoad";
            return string.IsNullOrEmpty(scene.name) ? "<untitled>" : scene.name;
        }

        private static string TypeName(Type type)
            => string.IsNullOrEmpty(type.Namespace) ? type.Name : type.FullName;

        private static bool IsStatic(MemberInfo member)
        {
            if (member is FieldInfo field)
                return field.IsStatic;
            if (member is PropertyInfo property)
                return property.GetAccessors(true).FirstOrDefault()?.IsStatic ?? false;
            if (member is MethodBase method)
                return method.IsStatic;
            return false;
        }

        private static bool CanWrite(MemberInfo member)
        {
            if (member is FieldInfo field)
                return !(field.IsLiteral && !field.IsInitOnly);
            if (member is PropertyInfo property)
                return property.CanWrite && property.GetIndexParameters().Length == 0;
            return false;
        }

        private static bool HasArguments(MemberInfo member)
        {
            if (member is MethodBase method)
                return method.GetParameters().Length > 0 || (member is MethodInfo mi && mi.IsGenericMethod);
            if (member is PropertyInfo property)
                return property.GetIndexParameters().Length > 0;
            return false;
        }

        private static Type GetMemberValueType(MemberInfo member)
        {
            if (member is FieldInfo field)
                return field.FieldType;
            if (member is PropertyInfo property)
                return property.PropertyType;
            if (member is MethodInfo method)
                return method.ReturnType;
            return null;
        }

        private static object GetInstance(MemberRecord record)
            => IsStatic(record.Member) ? null : record.Target;

        private static string GetHandle(object value)
        {
            if (value == null)
                return null;

            if (ReverseHandles.TryGetValue(value, out string existing))
                return existing;

            string prefix = value switch
            {
                GameObject => "obj",
                Component => "component",
                Type => "type",
                Scene => "scene",
                UnityEngine.Object => "unity",
                _ => "value"
            };
            string handle = $"{prefix}_{Guid.NewGuid():N}";
            Handles[handle] = value;
            ReverseHandles[value] = handle;
            return handle;
        }

        private static string GetMemberHandle(object target, MemberInfo member)
        {
            string handle = $"member_{Guid.NewGuid():N}";
            Members[handle] = new MemberRecord(target, member);
            return handle;
        }

        private static T Resolve<T>(string handle) where T : class
        {
            object value = ResolveAny(handle);
            if (value is T typed)
                return typed;
            throw new InvalidOperationException($"Handle '{handle}' is not a {typeof(T).Name}.");
        }

        private static object ResolveAny(string handle, bool throwIfMissing = true)
        {
            if (!string.IsNullOrEmpty(handle) && Handles.TryGetValue(handle, out object value))
            {
                if (value is UnityEngine.Object unityObject && unityObject.IsNullOrDestroyed())
                {
                    Handles.Remove(handle);
                    if (throwIfMissing)
                        throw new InvalidOperationException($"Handle '{handle}' points to a destroyed Unity object.");
                    return null;
                }
                return value;
            }

            if (throwIfMissing)
                throw new InvalidOperationException($"Unknown handle '{handle}'.");
            return null;
        }

        private static MemberRecord ResolveMember(Dictionary<string, object> args)
        {
            string memberHandle = GetString(args, "memberHandle");
            if (!string.IsNullOrEmpty(memberHandle) && Members.TryGetValue(memberHandle, out MemberRecord byHandle))
                return byHandle;

            object target = ResolveAny(GetString(args, "targetHandle"));
            string memberId = GetString(args, "memberId");
            if (string.IsNullOrEmpty(memberId))
                throw new InvalidOperationException("Provide memberHandle or targetHandle + memberId.");

            string[] parts = memberId.Split(':');
            string kind = parts.Length > 1 ? parts[0] : "";
            string name = parts.Length > 1 ? parts[1] : memberId;
            Type targetType = target as Type ?? target.GetActualType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

            MemberInfo member = null;
            if (kind.Equals("field", StringComparison.OrdinalIgnoreCase))
                member = targetType.GetField(name, flags);
            else if (kind.Equals("property", StringComparison.OrdinalIgnoreCase))
                member = targetType.GetProperty(name, flags);
            else if (kind.Equals("method", StringComparison.OrdinalIgnoreCase))
                member = targetType.GetMethods(flags).FirstOrDefault(it => it.Name == name);
            else
                member = targetType.GetMember(name, flags).FirstOrDefault();

            if (member == null)
                throw new InvalidOperationException($"Could not find member '{memberId}' on {targetType.FullName}.");

            return new MemberRecord(target, member);
        }

        private static List<object> Page<T>(List<T> items, int cursor, int limit, Func<T, object> convert)
        {
            List<object> page = new();
            for (int i = cursor; i < items.Count && page.Count < limit; i++)
                page.Add(convert(items[i]));
            return page;
        }

        private static int GetLimit(Dictionary<string, object> args)
            => Math.Max(1, Math.Min(GetInt(args, "limit", 25), MaxLimit));

        private static int GetCursor(Dictionary<string, object> args)
        {
            object value = GetValue(args, "cursor");
            if (value == null)
                return 0;
            if (value is int i)
                return Math.Max(0, i);
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return Math.Max(0, parsed);
            return 0;
        }

        private static Dictionary<string, object> Ok(object result)
            => Dict("ok", true, "result", result);

        private static Dictionary<string, object> Error(string code, string message)
            => Dict("ok", false, "error", Dict("code", code, "message", message));

        private static void WriteResponse(HttpListenerContext context, int status, object body)
        {
            string json = MiniJson.Serialize(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void WriteConnectionFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "unityexplorer-mcp");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "connection.json");
            File.WriteAllText(file, MiniJson.Serialize(Dict(
                "url", Url,
                "token", token,
                "product", Application.productName,
                "unityExplorerVersion", ExplorerCore.VERSION,
                "createdUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            )));
        }

        private static Dictionary<string, object> Dict(params object[] entries)
        {
            Dictionary<string, object> dict = new();
            for (int i = 0; i + 1 < entries.Length; i += 2)
                dict[entries[i].ToString()] = entries[i + 1];
            return dict;
        }

        private static Dictionary<string, object> AsDict(object value)
            => value as Dictionary<string, object> ?? new Dictionary<string, object>();

        private static List<object> GetList(object value)
            => value as List<object> ?? new List<object>();

        private static object GetValue(Dictionary<string, object> dict, string key)
            => dict != null && dict.TryGetValue(key, out object value) ? value : null;

        private static string GetString(Dictionary<string, object> dict, string key)
            => GetValue(dict, key)?.ToString();

        private static int GetInt(Dictionary<string, object> dict, string key, int fallback)
        {
            object value = GetValue(dict, key);
            if (value == null)
                return fallback;
            if (value is int i)
                return i;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object value = GetValue(dict, key);
            if (value == null)
                return fallback;
            if (value is bool b)
                return b;
            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : fallback;
        }

        private static List<string> GetStringList(object value)
        {
            if (value is List<object> list)
                return list.Select(item => item?.ToString()).Where(item => !string.IsNullOrEmpty(item)).ToList();
            if (value is string str && !string.IsNullOrEmpty(str))
                return str.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
            return new List<string>();
        }

        private sealed class BridgeWorkItem
        {
            public readonly string Method;
            public readonly Dictionary<string, object> Params;
            public readonly ManualResetEvent Done = new(false);
            public object Result;
            public Exception Error;

            public BridgeWorkItem(string method, Dictionary<string, object> args)
            {
                Method = method;
                Params = args;
            }
        }

        private sealed class MemberRecord
        {
            public readonly object Target;
            public readonly MemberInfo Member;

            public MemberRecord(object target, MemberInfo member)
            {
                Target = target;
                Member = member;
            }
        }

        private sealed class PendingAction
        {
            public string Kind;
            public string Description;
            public Func<object> Apply;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
