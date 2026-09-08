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
    /// Unity 的 Vector2 / Vector3 带有 normalized、magnitude 这类派生属性，返回的又是自身类型，
    /// Newtonsoft 顺着这些属性会无限递归并抛 "Self referencing loop"（例如 CharacterCard.RelativePos）。
    /// 这里统一把向量按 {x,y[,z]} 序列化，既不会循环，也能原样读回来。
    /// 以后再加 Quaternion / Color 等 Unity 结构体时，按同样的方式补一个 converter 即可。
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        Converters = new List<JsonConverter>
        {
            new Vector2Converter(),
            new Vector3Converter(),
        },
    };

    private class Vector2Converter : JsonConverter<Vector2>
    {
        public override void WriteJson(JsonWriter writer, Vector2 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
            writer.WriteEndObject();
        }

        public override Vector2 ReadJson(JsonReader reader, System.Type objectType, Vector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.StartObject) return existingValue;

            float x = existingValue.x;
            float y = existingValue.y;
            while (reader.Read() && reader.TokenType != JsonToken.EndObject)
            {
                if (reader.TokenType != JsonToken.PropertyName) continue;

                string prop = reader.Value as string;
                reader.Read();
                float value = reader.Value == null ? 0f : System.Convert.ToSingle(reader.Value);
                if (prop == "x") x = value;
                else if (prop == "y") y = value;
            }
            return new Vector2(x, y);
        }
    }

    private class Vector3Converter : JsonConverter<Vector3>
    {
        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
            writer.WritePropertyName("z");
            writer.WriteValue(value.z);
            writer.WriteEndObject();
        }

        public override Vector3 ReadJson(JsonReader reader, System.Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.StartObject) return existingValue;

            Vector3 v = existingValue;
            while (reader.Read() && reader.TokenType != JsonToken.EndObject)
            {
                if (reader.TokenType != JsonToken.PropertyName) continue;

                string prop = reader.Value as string;
                reader.Read();
                float value = reader.Value == null ? 0f : System.Convert.ToSingle(reader.Value);
                if (prop == "x") v.x = value;
                else if (prop == "y") v.y = value;
                else if (prop == "z") v.z = value;
            }
            return v;
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

            string json = JsonConvert.SerializeObject(data, prettyPrint ? Formatting.Indented : Formatting.None, JsonSettings);
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

            T data = JsonConvert.DeserializeObject<T>(json, JsonSettings);
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