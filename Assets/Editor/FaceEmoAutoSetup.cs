using System;
using System.IO;
using System.Linq;
using Suzuryg.FaceEmo.AppMain;
using Suzuryg.FaceEmo.UseCase;
using Suzuryg.FaceEmo.Components;
using Suzuryg.FaceEmo.Components.Data;
using Suzuryg.FaceEmo.Components.Settings;
using Suzuryg.FaceEmo.Domain;
using FaceEmoMenu = Suzuryg.FaceEmo.Domain.Menu;
using FaceEmoAnimation = Suzuryg.FaceEmo.Domain.Animation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

/// <summary>
/// シーン内の全アバターに対して Face Emo の表情セットアップを自動実行する。
/// 各アバターの prefab フォルダにある Menu/EX/*.anim を検索して登録する。
/// </summary>
public static class FaceEmoAutoSetup
{
    private const string LauncherObjectName = "FaceEmo";
    private const int PageSize = 8;
    private const int RegisteredCapacity = 7; // VRChat action menu: 8 items - 1 for FaceEmo settings

    [MenuItem("Tools/FaceEmo Auto Setup All Avatars")]
    public static void Run()
    {
        var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true); // 非アクティブも含む
        if (avatars.Length == 0)
        {
            EditorUtility.DisplayDialog("FaceEmo Auto Setup", "シーンにアバターが見つかりません。", "OK");
            return;
        }

        int succeeded = 0;
        int skipped = 0;
        foreach (var avatar in avatars)
        {
            if (SetupAvatar(avatar))
                succeeded++;
            else
                skipped++;
        }

        // 確実にデータが永続化されるようシーンを自動保存
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

        EditorUtility.DisplayDialog(
            "FaceEmo Auto Setup",
            $"完了\n成功: {succeeded} 体\nスキップ: {skipped} 体\n\n表情メニューと FX が自動生成されました。アバターをアップロードすればアクションメニューで表情が切り替えられます。",
            "OK"
        );
    }

    static bool SetupAvatar(VRCAvatarDescriptor avatar)
    {
        var avatarRoot = avatar.gameObject;

        // Prefab から EX フォルダを探す
        var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatarRoot);
        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogWarning($"[FaceEmoAutoSetup] {avatarRoot.name}: Prefabインスタンスではないためスキップ");
            return false;
        }

        var prefabDir = Path.GetDirectoryName(prefabPath).Replace("\\", "/");
        var exFolderAssetPath = FindEXFolderAssetPath(prefabDir);

        if (exFolderAssetPath == null)
        {
            Debug.LogWarning($"[FaceEmoAutoSetup] {avatarRoot.name}: Menu/EX フォルダが見つかりません ({prefabDir})");
            return false;
        }

        // EX フォルダ内の全 .anim を収集
        var exFolderAbsolute = AssetPathToAbsolute(exFolderAssetPath);
        var animAssetPaths = Directory
            .GetFiles(exFolderAbsolute, "*.anim", SearchOption.AllDirectories)
            .Select(p => AbsoluteToAssetPath(p))
            .OrderBy(p => p)
            .ToArray();

        if (animAssetPaths.Length == 0)
        {
            Debug.LogWarning($"[FaceEmoAutoSetup] {avatarRoot.name}: .anim が見つかりません ({exFolderAssetPath})");
            return false;
        }

        // Face Emo ランチャー子オブジェクトを作成/取得し、コンポーネントを初期化
        var launcherObj = GetOrCreateLauncher(avatarRoot);
        var installer = new FaceEmoInstaller(launcherObj);

        // アバターターゲットを設定
        var launcher = launcherObj.GetComponent<FaceEmoLauncherComponent>();
        Undo.RegisterCompleteObjectUndo(launcher, "FaceEmo Auto Setup");
        launcher.AV3Setting.TargetAvatar = avatar;
        launcher.AV3Setting.TargetAvatarPath = "/" + avatarRoot.name;
        EditorUtility.SetDirty(launcher);
        EditorUtility.SetDirty(launcher.AV3Setting);

        // メニューを構築
        var menu = BuildMenu(animAssetPaths, exFolderAssetPath);
        Debug.Log($"[FaceEmoAutoSetup] {avatarRoot.name}: {animAssetPaths.Length} アニメ → Registered {menu.Registered.Order.Count} アイテム");

        // MenuRepository.Save() と同じ手順でメニューデータを保存
        var menuRepoComp = launcherObj.GetComponent<MenuRepositoryComponent>();
        Undo.RegisterCompleteObjectUndo(menuRepoComp, "FaceEmo Auto Setup");
        menuRepoComp.SerializableMenu = ScriptableObject.CreateInstance<SerializableMenu>();
        menuRepoComp.SerializableMenu.Save(menu, isAsset: false);
        EditorUtility.SetDirty(menuRepoComp);

        // Generate FX: FX アニメーターと Modular Avatar プレハブを生成
        bool wasActive = avatarRoot.activeSelf;
        if (!wasActive) avatarRoot.SetActive(true);
        try
        {
            var fxGenerator = installer.Container.Resolve<IFxGenerator>();
            var editablePaths = fxGenerator.GetParentPrefabPathOfMARootObjects();
            fxGenerator.Generate(menu, editablePaths);
            Debug.Log($"[FaceEmoAutoSetup] {avatarRoot.name}: Generate FX 完了");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FaceEmoAutoSetup] {avatarRoot.name}: Generate FX 失敗: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (!wasActive) avatarRoot.SetActive(false);
        }

        Debug.Log($"[FaceEmoAutoSetup] {avatarRoot.name}: セットアップ完了");
        return true;
    }

    static FaceEmoMenu BuildMenu(string[] animAssetPaths, string exFolderAssetPath)
    {
        var menu = new FaceEmoMenu();

        // サブフォルダがあればフォルダ別にグループ化、なければ枚数で自動グループ化
        bool hasSubfolders = animAssetPaths.Any(p =>
        {
            var rel = p.Substring(exFolderAssetPath.Length + 1);
            return rel.Contains('/');
        });

        if (hasSubfolders)
        {
            var groups = animAssetPaths
                .GroupBy(p =>
                {
                    var rel = p.Substring(exFolderAssetPath.Length + 1);
                    var slash = rel.IndexOf('/');
                    return slash >= 0 ? rel.Substring(0, slash) : "";
                })
                .ToArray();

            foreach (var grp in groups)
            {
                if (!menu.CanAddMenuItemTo(FaceEmoMenu.RegisteredId)) break;

                if (grp.Key == "")
                {
                    AddModesToList(menu, FaceEmoMenu.RegisteredId, grp.ToArray());
                }
                else
                {
                    var groupId = menu.AddGroup(FaceEmoMenu.RegisteredId);
                    menu.ModifyGroupProperties(groupId, displayName: grp.Key);
                    AddModesToList(menu, groupId, grp.ToArray());
                }
            }
        }
        else if (animAssetPaths.Length <= RegisteredCapacity)
        {
            // 7件以下はそのままページ1に並べる
            AddModesToList(menu, FaceEmoMenu.RegisteredId, animAssetPaths);
        }
        else
        {
            // 8件ごとにグループ化（A, B, C ...）
            var chunks = animAssetPaths
                .Select((p, i) => new { p, i })
                .GroupBy(x => x.i / PageSize)
                .Select((g, gi) => new
                {
                    name = ((char)('A' + gi)).ToString(),
                    paths = g.Select(x => x.p).ToArray()
                })
                .ToArray();

            foreach (var chunk in chunks)
            {
                if (!menu.CanAddMenuItemTo(FaceEmoMenu.RegisteredId)) break;
                var groupId = menu.AddGroup(FaceEmoMenu.RegisteredId);
                menu.ModifyGroupProperties(groupId, displayName: chunk.name);
                AddModesToList(menu, groupId, chunk.paths);
            }
        }

        return menu;
    }

    static void AddModesToList(FaceEmoMenu menu, string listId, string[] animAssetPaths)
    {
        foreach (var assetPath in animAssetPaths)
        {
            if (!menu.CanAddMenuItemTo(listId)) break;

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) continue;

            var modeId = menu.AddMode(listId);
            var displayName = Path.GetFileNameWithoutExtension(assetPath);
            menu.ModifyModeProperties(modeId, displayName: displayName, useAnimationNameAsDisplayName: false);
            menu.SetAnimation(new FaceEmoAnimation(guid), modeId);
        }
    }

    static GameObject GetOrCreateLauncher(GameObject avatarRoot)
    {
        var existing = avatarRoot.transform.Find(LauncherObjectName);
        if (existing != null) return existing.gameObject;

        var obj = new GameObject(LauncherObjectName);
        obj.transform.SetParent(avatarRoot.transform, false);
        Undo.RegisterCreatedObjectUndo(obj, "Create FaceEmo Launcher");
        return obj;
    }

    // "Assets/Foo/Bar" を返す。見つからなければ null。
    static string FindEXFolderAssetPath(string prefabDir)
    {
        var candidates = new[]
        {
            $"{prefabDir}/Menu/EX",
            $"{prefabDir}/Expressions",
            $"{prefabDir}/EX",
        };
        foreach (var c in candidates)
        {
            if (AssetDatabase.IsValidFolder(c)) return c;
        }

        // 同キャラ別コスチューム対応: 親フォルダ内の兄弟フォルダを名前プレフィックスで検索
        // 例: Lucifer-1.5.3 → Lucifer_Apron-1.0.1/Menu/EX を探す
        var folderName = Path.GetFileName(prefabDir);
        var parentDir = Path.GetDirectoryName(prefabDir).Replace("\\", "/");
        var prefix = folderName.Split('-')[0].Split('_')[0]; // "Lucifer-1.5.3" → "Lucifer"
        if (!string.IsNullOrEmpty(prefix) && AssetDatabase.IsValidFolder(parentDir))
        {
            foreach (var sibling in AssetDatabase.GetSubFolders(parentDir))
            {
                if (sibling == prefabDir) continue;
                var siblingName = Path.GetFileName(sibling);
                if (!siblingName.StartsWith(prefix)) continue;
                foreach (var fallback in new[] { $"{sibling}/Menu/EX", $"{sibling}/Expressions", $"{sibling}/EX" })
                {
                    if (AssetDatabase.IsValidFolder(fallback))
                    {
                        Debug.Log($"[FaceEmoAutoSetup] {folderName}: EXフォルダなし → {siblingName} のフォルダを使用");
                        return fallback;
                    }
                }
            }
        }

        return null;
    }

    static string AssetPathToAbsolute(string assetPath)
    {
        var dataPath = Application.dataPath.Replace("\\", "/");
        return dataPath + assetPath.Substring("Assets".Length);
    }

    static string AbsoluteToAssetPath(string absolutePath)
    {
        var dataPath = Application.dataPath.Replace("\\", "/");
        return "Assets" + absolutePath.Replace("\\", "/").Substring(dataPath.Length);
    }
}
