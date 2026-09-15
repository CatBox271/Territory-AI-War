#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WallManager))]
public class WallManagerEditor : Editor
{
    private SerializedProperty sturctProp;
    private SerializedProperty insideProp;
    private int lastCount;

    private void OnEnable()
    {
        sturctProp = serializedObject.FindProperty("sturct");
        insideProp = sturctProp.FindPropertyRelative("Inside");
        lastCount = insideProp.arraySize;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 绘制默认 Inspector（包含所有字段）
        DrawDefaultInspector();

        // 检查数组大小是否增加（即添加了新元素）
        int newCount = insideProp.arraySize;
        if (newCount > lastCount)
        {
            // 新添加的元素在末尾
            var newElement = insideProp.GetArrayElementAtIndex(newCount - 1);
            var wallProp = newElement.FindPropertyRelative("wall");

            // 重置四个 Transform 引用为 null，避免复制前一项的引用
            wallProp.FindPropertyRelative("UP").objectReferenceValue = null;
            wallProp.FindPropertyRelative("DOWN").objectReferenceValue = null;
            wallProp.FindPropertyRelative("LEFT").objectReferenceValue = null;
            wallProp.FindPropertyRelative("RIGHT").objectReferenceValue = null;

            // 立即应用修改，防止后续操作干扰
            serializedObject.ApplyModifiedProperties();
        }
        lastCount = newCount;

        serializedObject.ApplyModifiedProperties();
    }
}
#endif