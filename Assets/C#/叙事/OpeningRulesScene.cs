using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class OpeningRulesScene : StoryScene
{
	private class Beat
	{
		public string big;

		public float bigSize;

		public string small;

		public string icon;

		public float hold;
	}

	private static readonly Beat[] Beats = new Beat[7]
	{
		new Beat
		{
			big = "打中就是你的",
			bigSize = 0.8f,
			icon = "领土",
			hold = 1.2f,
			small = "子弹把数值涂成你的颜色——地图就是比分"
		},
		new Beat
		{
			big = "大球越大，越重",
			bigSize = 0.8f,
			icon = "大球",
			hold = 1.2f,
			small = "撞飞对手、碾过防线；堆子弹也能把它顶偏"
		},
		new Beat
		{
			big = "炮塔只有一条命",
			bigSize = 0.8f,
			hold = 1.2f,
			small = "一发命中就出局——护盾挡得住一切，挡不住穿甲弹"
		},
		new Beat
		{
			big = "穿甲弹，命中即死",
			bigSize = 0.78f,
			icon = "穿甲",
			hold = 1.2f,
			small = "无视护盾直取炮塔，杀完还能继续飞"
		},
		new Beat
		{
			big = "弹珠滚过倍区，变成武器",
			bigSize = 0.68f,
			icon = "升级",
			hold = 1.2f,
			small = "×2 ×4 ×8 ｜ 空槽攒满，三选一"
		},
		new Beat
		{
			big = "你看见的，都是几秒前",
			bigSize = 0.72f,
			icon = "延迟",
			hold = 1.2f,
			small = "情报滞后——台面之下还能密约结盟"
		},
		new Beat
		{
			big = "一种颜色吞掉地图，才算终局",
			bigSize = 0.6f,
			hold = 1.35f,
			small = "你想让谁活到最后？"
		}
	};

	private const float TitleHold = 1.5f;

	private const float BigY = 0.42f;

	private const float SmallY = -0.62f;

	private const float BarY = -0.1f;

	private const float BarW = 2.6f;

	private const float TitleBigY = 0.65f;

	private const float TitleSubY = -0.7f;

	private const float TitleBarY = -0.18f;

	private const float TitleBarW = 3.4f;

	private const float IconX = -5.75f;

	private const float IconPlate = 2.3f;

	private const float IconSelf = 1.7f;

	private const float InDur = 0.17f;

	private const float OutDur = 0.14f;

	private const float BigDist = 3.2f;

	private const float SmallDist = 2.2f;

	private const float SmallDelay = 0.06f;

	private const float CutGap = 0.05f;

	private static readonly Color Accent = StageStyle.Mark;

	private const string SmallColor = "#AEB9CC";

	private static string Hex => ColorUtility.ToHtmlStringRGB(Accent);

	public override IEnumerator Play()
	{
		StoryTeller s = StoryScene.stage;
		if (!((Object)(object)s == (Object)null))
		{
			yield return TitlePage(s);
			for (int i = 0; i < Beats.Length; i++)
			{
				yield return BeatPage(s, Beats[i], i);
			}
		}
	}

	private IEnumerator TitlePage(StoryTeller s)
	{
		int num = (((Object)(object)MapConfig.Instance != (Object)null && MapConfig.Instance.teamColors != null) ? Mathf.Max(1, MapConfig.Instance.teamColors.Count - 1) : 4);
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 1; i <= num; i++)
		{
			if (i > 1)
			{
				stringBuilder.Append("  ·  ");
			}
			stringBuilder.Append(AIAgent.GetStageName(i));
		}
		StoryTeller.Item big = StoryScene.Label(StageStyle.SizeTag(1f) + "<color=#" + Hex + "><b>领 土 战 争</b></color></size>", new Vector2(0f, 0.65f), new Vector2(16f, 1.4f), Accent, StageStyle.FontSize(1f));
		StoryTeller.Item bar = StoryScene.AccentBar(Accent, new Vector2(0f, -0.18f), 3.4f, 0.1f);
		StoryTeller.Item small = StoryScene.Label(StageStyle.SizeTag(0.4f) + "<color=#AEB9CC>AI 实况对局 ｜ </color>" + StoryScene.Colorize(stringBuilder.ToString()) + "</size>", new Vector2(0f, -0.7f), new Vector2(16f, 1f), StageStyle.TextDim, StageStyle.FontSize(0.4f));
		((MonoBehaviour)s).StartCoroutine(s.SlideIn(big, StoryTeller.Direction.Top, 3.5f, 0.2f, 0f, (Vector2?)new Vector2(0.82f, 0.82f)));
		((MonoBehaviour)s).StartCoroutine(s.Pop(bar, new Vector2(0.02f, 1f), Vector2.one, 0.18f));
		yield return s.WaitStage(0.06f);
		yield return s.SlideIn(small, StoryTeller.Direction.Botton, 2.2f, 0.17f);
		yield return s.WaitStage(1.5f);
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(big, StoryTeller.Direction.Top, 3f, 0.14f));
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(bar, StoryTeller.Direction.Top, 2f, 0.14f));
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(small, StoryTeller.Direction.Botton, 2f, 0.14f));
		yield return s.WaitStage(0.05f);
	}

	private IEnumerator BeatPage(StoryTeller s, Beat b, int index)
	{
		StoryTeller.Direction from = ((index % 2 != 0) ? StoryTeller.Direction.Right : StoryTeller.Direction.Left);
		List<StoryTeller.Item> icons = new List<StoryTeller.Item>();
		Sprite val = Icon(b.icon);
		if ((Object)(object)val != (Object)null)
		{
			StoryTeller.Item item = StoryScene.Picture(null, new Vector2(-5.75f, 0.42f), new Vector2(2.3f, 2.3f), Color.white, panel: true);
			if (item != null)
			{
				item.cornerRadius = 0.55f;
				item.containerColor = new Color(Accent.r, Accent.g, Accent.b, 0.1f);
				item.sortingOrder = 1;
				s.ApplyItem(item);
				icons.Add(item);
			}
			StoryTeller.Item item2 = StoryScene.Picture(val, new Vector2(-5.75f, 0.42f), new Vector2(1.7f, 1.7f), Color.white);
			if (item2 != null)
			{
				item2.sortingOrder = 2;
				s.ApplyItem(item2);
				icons.Add(item2);
			}
		}
		StoryTeller.Item big = StoryScene.Label(StageStyle.SizeTag(b.bigSize) + "<color=#" + Hex + "><b>" + b.big + "</b></color></size>", new Vector2(0f, 0.42f), new Vector2(16f, 1.3f), Accent, StageStyle.FontSize(b.bigSize));
		StoryTeller.Item bar = StoryScene.AccentBar(Accent, new Vector2(0f, -0.1f), 2.6f, 0.1f);
		StoryTeller.Item small = StoryScene.Label(StageStyle.SizeTag(0.36f) + "<color=#AEB9CC>" + b.small + "</color></size>", new Vector2(0f, -0.62f), new Vector2(16f, 1f), StageStyle.TextDim, StageStyle.FontSize(0.36f));
		foreach (StoryTeller.Item item3 in icons)
		{
			((MonoBehaviour)s).StartCoroutine(s.SlideIn(item3, from, 4f, 0.2f, 0f, (Vector2?)new Vector2(0.7f, 0.7f)));
		}
		((MonoBehaviour)s).StartCoroutine(s.SlideIn(big, from, 3.2f, 0.17f, 0.25f, (Vector2?)new Vector2(1.16f, 1.16f)));
		((MonoBehaviour)s).StartCoroutine(s.Pop(bar, new Vector2(0.02f, 1f), Vector2.one, 0.16f));
		yield return s.WaitStage(0.06f);
		yield return s.SlideIn(small, StoryTeller.Direction.Botton, 2.2f, 0.17f);
		yield return s.WaitStage(b.hold);
		foreach (StoryTeller.Item item4 in icons)
		{
			((MonoBehaviour)s).StartCoroutine(s.SlideOut(item4, from, 3.4f, 0.14f));
		}
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(big, from, 2.8f, 0.14f));
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(bar, from, 2.2f, 0.14f));
		((MonoBehaviour)s).StartCoroutine(s.SlideOut(small, StoryTeller.Direction.Botton, 2.2f, 0.14f));
		yield return s.WaitStage(0.05f);
	}

	private static Sprite Icon(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		Sprite obj = Resources.Load<Sprite>("规则/" + key);
		if ((Object)(object)obj == (Object)null)
		{
			Debug.LogWarning((object)("[开场看点] 缺图标：Assets/Resources/规则/" + key + ".png（这一屏先不放）"));
		}
		return obj;
	}
}
