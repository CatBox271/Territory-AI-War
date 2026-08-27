using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class Situation
{
    public class IBall
    {
        string num;
        int uuid;
        float radius;
        Vector2 pos;
        Vector2 velocity;
        private GameObject ob;
        private IStageValue sv;
        private Rigidbody2D rb;
        public IBall(GameObject go)
        {
            ob = go;
            sv = go.GetComponent<IStageValue>();
            uuid = go.GetInstanceID();
            rb = go.GetComponent<Rigidbody2D>();
            Refresh();
        }
        void Refresh()
        {
            num = sv.value.ToShortString();
            radius = ob.transform.lossyScale.x;
            pos = rb.position;
            velocity = rb.velocity;
        }
        public bool AliveCheck()
        {
            if (ob == null) return false;
            Refresh();
            return true;
        }
        public override string ToString()
        {
            return string.Format("(总量{0}的球，半径为{1}，坐标{2}，速度{3}，唯一码:{4})", num, radius, pos, velocity, uuid);
        }
    }

    public class IBalls
    {
        int stage;
        public List<IBall> balls = new();

        public void AddNewBall(List<GameObject> ball_list)
        {
            foreach (var ball in ball_list)
                balls.Add(new IBall(ball));
        }

        public void Refresh()
        {
            for (int i = balls.Count - 1; i > -1; i--)
                if (!balls[i].AliveCheck()) balls.RemoveAt(i);
        }

        public override string ToString()
        {
            Refresh();
            var final = new StringBuilder();
            final.Append($"【{stage}号阵营球的信息：");
            foreach (var ball in balls)
                final.Append(ball.ToString());
            final.Append("】");
            return final.ToString();
        }
    }

    public class IShield
    {
        int stage;
        string valueStr;
        float radius;
        Vector2 pos;
        bool active;
        private GameObject ob;
        private IStageValue sv;

        public IShield(GameObject go, int stage)
        {
            this.stage = stage;
            ob = go;
            sv = go.GetComponent<IStageValue>();
            Refresh();
        }

        public void Refresh()
        {
            if (ob == null) return;
            active = ob.activeSelf;
            if (!active) return;
            valueStr = sv.value.ToShortString();
            radius = ob.transform.lossyScale.x;
            pos = ob.transform.position;
        }

        public bool AliveCheck()
        {
            if (ob == null) return false;
            Refresh();
            return true;
        }

        public override string ToString()
        {
            if (!active) return string.Format("【{0}号护盾: 已破碎】", stage);
            return string.Format("【{0}号护盾: 值{1}，半径{2:F1}，位置({3:F1},{4:F1})】", stage, valueStr, radius, pos.x, pos.y);
        }
    }

    public class IShields
    {
        IShield[] shields = new IShield[4];

        public void Bind(int stage, GameObject shieldObj)
        {
            shields[stage - 1] = new IShield(shieldObj, stage);
        }

        public void Refresh()
        {
            for (int i = 0; i < 4; i++)
                if (shields[i] != null && !shields[i].AliveCheck())
                    shields[i] = null;
        }

        public override string ToString()
        {
            Refresh();
            var final = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                if (shields[i] != null)
                {
                    final.Append(shields[i].ToString());
                    final.Append('\n');
                }
            }
            return final.Length > 0 ? final.ToString() : "无护盾信息\n";
        }
    }

    public IBalls[] Ball;
    public IShields Shields = new();
    public string MapDescription;

    public string GetSituation(int stage)
    {
        var final = new StringBuilder();
        stage--;

        var allMarbles = Object.FindObjectsOfType<Marble>();
        int aliveCount = 0;
        var values = new StringBuilder();
        foreach (var m in allMarbles)
        {
            if (m == null || !m.gameObject.activeSelf || m.stage != stage + 1) continue;
            if (aliveCount > 0) values.Append(", ");
            values.Append(HugeInt.Pow(2, (int)m.ValueExponent).ToShortString());
            aliveCount++;
        }
        final.Append($"弹珠: 共{aliveCount}个");
        if (aliveCount > 0)
        {
            final.Append(" (");
            final.Append(values);
            final.Append(')');
        }
        final.Append("\n\n");

        var props = MapConfig.Instance.teamProps[stage + 1];
        final.Append($"道具栈(上限{MapConfig.Instance.propLimit}): ");
        if (props.Count == 0)
        {
            final.Append("空");
        }
        else
        {
            for (int i = 0; i < props.Count; i++)
            {
                if (i > 0) final.Append(" | ");
                final.Append($"{props[i].item}({props[i].value.ToShortString()})");
            }
        }
        final.Append("\n超出的道具会被自动使用\n\n");

        final.Append("各队护盾：\n");
        final.Append(Shields.ToString());

        final.Append("\n当前地图情况：");
        final.Append(MapDescription);

        return final.ToString();
    }
}
