using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

public class WallManager : MonoBehaviour
{

    [System.Serializable]
    public class CloseTo
    {
        public bool up = false;
        public bool down = false;
        public bool left = false;
        public bool right = false;
        public CloseTo() { }

        public Vector3 AdjustPos(WallSturct father,WallSturct self)
        {
            Vector3 pos = new();
            if (up ^ down)
            {
                if (up)
                {
                    pos.y = (father.height - self.height) / 2f;
                }
                else
                {
                    pos.y = (father.height - self.height) / -2f;
                }
            }
            if (left ^ right)
            {
                if (right)
                {
                    pos.x = (father.width - self.width) / 2f;
                }
                else
                {
                    pos.x = (father.width - self.width) / -2f;
                }
            }
            
            return pos;
        }
    }
    [System.Serializable]
    public class WallHide
    {
        public bool show_up = true;
        public bool show_down = true;
        public bool show_left = true;
        public bool show_right = true;
        public Transform UP;
        public Transform DOWN;
        public Transform LEFT;
        public Transform RIGHT;
        public WallHide() { }
    }
    [System.Serializable]
    public class WallSturct
    {
        public float width = 0;
        public float height = 0;
        public float wall_width = 0;
        public Vector2 pos = new();
        public CloseTo adjust = new();
        public WallHide wall = new();
        public List<WallSturct> Inside = new();

        public WallSturct() { }
        
        public void Adjust(WallManager grandmother, Vector3 father_pos)
        {
            float x = (width + wall_width) / 2f;
            float y = (height + wall_width) / 2f;
            Vector3 self = father_pos + (Vector3)pos;
            if (wall.show_up)
            {
                if (wall.UP == null) wall.UP = Instantiate(grandmother.MonoWall, grandmother.transform).transform;
                wall.UP.position = self + new Vector3(0, y);
                wall.UP.localScale = new Vector3(width + wall_width * 2, wall_width);
                wall.UP.localEulerAngles = new(0, 0, 0);
                wall.UP.gameObject.SetActive(true);
            }
            else
            {
                if (wall.UP != null) wall.UP.gameObject.SetActive(false);
            }
            if (wall.show_down)
            {
                if (wall.DOWN == null) wall.DOWN = Instantiate(grandmother.MonoWall, grandmother.transform).transform;
                wall.DOWN.position = self - new Vector3(0, y);
                wall.DOWN.localScale = new Vector3(width + wall_width * 2, wall_width);
                wall.DOWN.localEulerAngles = new(0, 0, 180);
                wall.DOWN.gameObject.SetActive(true);
            }
            else
            {
                if (wall.DOWN != null) wall.DOWN.gameObject.SetActive(false);
            }
            if (wall.show_left)
            {
                if (wall.LEFT == null) wall.LEFT = Instantiate(grandmother.MonoWall, grandmother.transform).transform;
                wall.LEFT.position = self - new Vector3(x, 0);
                wall.LEFT.localScale = new Vector3(height + wall_width * 2, wall_width);
                wall.LEFT.localEulerAngles = new(0, 0, -90);
                wall.LEFT.gameObject.SetActive(true);
            }
            else
            {
                if (wall.LEFT != null) wall.LEFT.gameObject.SetActive(false);
            }
            if (wall.show_right)
            {
                if (wall.RIGHT == null) wall.RIGHT = Instantiate(grandmother.MonoWall, grandmother.transform).transform;
                wall.RIGHT.position = self + new Vector3(x, 0);
                wall.RIGHT.localScale = new Vector3(height + wall_width * 2, wall_width);
                wall.RIGHT.localEulerAngles = new(0, 0, 90);
                wall.RIGHT.gameObject.SetActive(true);
            }
            else
            {
                if (wall.RIGHT != null) wall.RIGHT.gameObject.SetActive(false);
            }


            foreach (var wall in Inside)
            {
                wall.Adjust(grandmother, self + wall.adjust.AdjustPos(this, wall));
            }
        }
    }

    public GameObject MonoWall;
    [SerializeField]
    public WallSturct sturct = new();

    private void OnValidate()
    {
        sturct.Adjust(this, transform.position);
    }
}

#if UNITY_EDITOR

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
