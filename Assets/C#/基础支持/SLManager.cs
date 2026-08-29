using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Text;
using Newtonsoft.Json;

public class SLManager : MonoBehaviour
{
    static string _persistentDataPath = "";

    static string persistentDataPath
    {
        get
        {
            if (_persistentDataPath == "") _persistentDataPath = Application.persistentDataPath;
            return _persistentDataPath;
        }
    }

    /// <summary>
    /// 导出任意对象到JSON文件
    /// </summary>
    public static string ExportToJson<T>(T data, string folderPath = "", string fileName = null, bool prettyPrint = true)
    {
        try
        {
            if (data == null)
            {
                Debug.LogWarning("导出数据为空");
                return null;
            }

            string json = JsonConvert.SerializeObject(data, prettyPrint ? Formatting.Indented : Formatting.None);
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("JSON序列化失败");
                return null;
            }

            string actualFileName = string.IsNullOrEmpty(fileName) ?
                GenerateDefaultFileName() : fileName;

            string basePath = persistentDataPath;

            if (!string.IsNullOrEmpty(folderPath))
            {
                basePath = Path.Combine(basePath, folderPath);
            }

            if (!Directory.Exists(basePath))
            {
                Directory.CreateDirectory(basePath);
            }

            string filePath = Path.Combine(basePath, actualFileName);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            return filePath;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"导出失败: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    private static string GenerateDefaultFileName()
    {
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return $"conversation_{timestamp}.json";
    }

    public static T ImportFromJson<T>(string folderPath, string fileName) where T : new()
    {
        try
        {
            string basePath = Path.Combine(persistentDataPath, folderPath, fileName);
            if (string.IsNullOrEmpty(basePath))
            {
                Debug.LogWarning("文件路径错误");
                return default;
            }

            if (!File.Exists(basePath))
            {
                Debug.LogWarning($"文件不存在: {basePath}");
                return default;
            }

            string json = File.ReadAllText(basePath, Encoding.UTF8);
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("文件为空");
                return default;
            }

            T data = JsonConvert.DeserializeObject<T>(json);
            if (data == null)
            {
                Debug.LogWarning("文件解析失败");
                return default;
            }

            Debug.Log($"<color=green>✓ {typeof(T).Name} 导入成功</color>\n路径: {basePath}");
            return data;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"导入失败: {e.Message}");
            return default;
        }
    }
}