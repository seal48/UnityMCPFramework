using System;
using System.Collections.Generic;
using GameFramework.Log;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.UI
{
    /// <summary>引导洞口形状。竖切/异形/多目标暂不支持（见 Game/Guide/README.md 的已知限制）。</summary>
    public enum GuideHoleShape
    {
        /// <summary>轴对齐矩形（默认）。</summary>
        Rect = 0,

        /// <summary>圆角矩形（角半径由配表 Radius 列给）。</summary>
        RoundRect = 1,

        /// <summary>正圆（头像用）。内部归一成正方形，保证是正圆而不是椭圆。</summary>
        Circle = 2,
    }

    /// <summary>
    /// 新手引导遮罩（AOT 表现层）：挖洞高亮 + 提示气泡 + 跳过按钮 + 点击拦截。
    ///
    /// 为什么是「运行时自建 UI」而不是预制体：
    ///   1) 引导要挂在 UILayer.Top（UIRoot 已经是 DontDestroyOnLoad 的常驻根），层级天然最高；
    ///   2) 不用往 Assets/Prefabs 加资产 → 不用重建资源包，也不用跑 UI 绑定生成；
    ///   3) 遮罩没有美术需求（纯色 + 形状 + 文本），代码搭出来即可。
    /// 要做成「换皮预制体」时，把 Show 的实现换成 Panel 子类 + 预制体即可，上层接口不变。
    ///
    /// 洞形的实现（不写 shader、不依赖美术切图）：
    ///   · Rect      —— 4 块 Image 拼矩形遮罩（最快路径）；
    ///   · RoundRect —— 4 块矩形遮罩铺在洞外侧 + 一块**运行时生成的圆角镂空贴图**盖在洞上；
    ///   · Circle    —— 同上，但洞先归一成正方形（保证正圆），生成时角半径 = 半边长。
    /// 生成的贴图是「形状内部透明、外部不透明」的 alpha 图，用 SDF 算出来并做 1~2 像素羽化，
    /// 所以边缘是抗锯齿的；尺寸按洞的屏幕尺寸生成（上限 512），按形状/尺寸缓存复用。
    /// 描边同理：用「形状边界内缩一圈色带」的 alpha 图，颜色和呼吸透明度由 Image.color 控制。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuideOverlay : MonoBehaviour
    {
        private const float DimAlpha = 0.62f;
        private const float HolePadding = 10f;
        private const float BorderThickness = 3f;
        private const float TipWidth = 860f;
        private const float TipHeight = 170f;
        private const int MaxShapeTextureSize = 512;

        private static readonly Color DimColor = new Color(0f, 0f, 0f, DimAlpha);
        private static readonly Color BorderColor = new Color(1f, 0.85f, 0.3f, 0.95f);
        private static readonly Color TipBgColor = new Color(0.04f, 0.05f, 0.09f, 0.94f);

        private static GuideOverlay instance;

        private RectTransform root;
        private RectTransform dimTop, dimBottom, dimLeft, dimRight;
        private Image borderTop, borderBottom, borderLeft, borderRight;
        private Image shapeMask;
        private Image shapeOutline;
        private RectTransform tipRoot;
        private Text tipText;
        private RectTransform skipRoot;
        private Image skipImage;
        private Text skipText;
        private RectTransform clickProxy;

        private Font font;
        private RectTransform target;
        private Action onTargetClicked;
        private Action onSkipClicked;
        private GuideHoleShape shape = GuideHoleShape.Rect;
        private int shapeRadius;

        private readonly Dictionary<string, Sprite> shapeSprites = new Dictionary<string, Sprite>();

        private bool built;
        private bool hasHole;

        /// <summary>当前遮罩实例（没有则 null）。</summary>
        public static GuideOverlay Instance { get { return instance; } }

        /// <summary>遮罩是否正在显示。</summary>
        public bool IsVisible { get { return gameObject != null && gameObject.activeSelf; } }

        /// <summary>拿到（必要时创建）遮罩。font 传 null 会退到内置字体（中文会显示成方块，业务应传当前语言字体）。</summary>
        public static GuideOverlay Ensure(Font font)
        {
            if (instance != null)
            {
                if (font != null) instance.font = font;
                return instance;
            }

            UIRoot uiRoot = UIRoot.Instance;
            if (uiRoot == null)
            {
                GameLog.Warn(LogTag.Panel, "引导遮罩创建失败：UIRoot 还没准备好（应在 UI 初始化之后再用）。");
                return null;
            }

            RectTransform layer = uiRoot.GetLayer(UILayer.Top);
            if (layer == null)
            {
                GameLog.Warn(LogTag.Panel, "引导遮罩创建失败：UIRoot 上取不到 Top 层。");
                return null;
            }

            GameObject go = new GameObject("GuideOverlay", typeof(RectTransform));
            go.transform.SetParent(layer, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            GuideOverlay overlay = go.AddComponent<GuideOverlay>();
            overlay.font = font != null ? font : BuiltinFont();
            overlay.Build();
            instance = overlay;
            return overlay;
        }

        /// <summary>销毁遮罩（切场景 / 测试重置用）。</summary>
        public static void DestroyInstance()
        {
            if (instance == null) return;
            UnityEngine.Object.Destroy(instance.gameObject);
            instance = null;
        }

        /// <summary>
        /// 显示一步引导。
        /// </summary>
        /// <param name="target">要高亮的目标节点；传 null 表示「全屏遮罩」（纯对话步）。</param>
        /// <param name="tip">提示文本（已本地化后的最终字符串）。</param>
        /// <param name="showSkip">是否显示「跳过引导」按钮。</param>
        /// <param name="interceptClick">是否在洞口放透明代理按钮（true = 只允许点目标）。</param>
        /// <param name="shape">洞形：矩形 / 圆角矩形 / 正圆。</param>
        /// <param name="radius">圆角半径（仅 RoundRect 用，像素）。</param>
        /// <param name="onTargetClicked">洞口被点（或无目标时整屏被点）时回调。</param>
        /// <param name="onSkip">点跳过按钮的回调。</param>
        public void Show(RectTransform target, string tip, bool showSkip, bool interceptClick,
            GuideHoleShape shape, int radius, Action onTargetClicked, Action onSkip)
        {
            if (!built) Build();

            this.target = target;
            this.onTargetClicked = onTargetClicked;
            this.onSkipClicked = onSkip;
            this.shape = shape;
            this.shapeRadius = radius;
            hasHole = target != null;

            gameObject.SetActive(true);

            SetActive(tipRoot, !string.IsNullOrEmpty(tip));
            if (tipText != null) tipText.text = tip ?? string.Empty;

            SetActive(skipRoot, showSkip && onSkip != null);

            clickProxy.gameObject.SetActive(interceptClick);

            if (hasHole) LayoutHole();
            else LayoutFull();
        }

        /// <summary>收起遮罩。</summary>
        public void Hide()
        {
            target = null;
            hasHole = false;
            onTargetClicked = null;
            onSkipClicked = null;
            if (gameObject != null) gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<string, Sprite> pair in shapeSprites)
            {
                if (pair.Value != null)
                {
                    if (pair.Value.texture != null) UnityEngine.Object.Destroy(pair.Value.texture);
                    UnityEngine.Object.Destroy(pair.Value);
                }
            }
            shapeSprites.Clear();

            if (instance == this) instance = null;
        }

        private void Build()
        {
            if (built) return;
            built = true;

            root = (RectTransform)transform;

            dimTop = CreateImage("DimTop", root, DimColor, true).rectTransform;
            dimBottom = CreateImage("DimBottom", root, DimColor, true).rectTransform;
            dimLeft = CreateImage("DimLeft", root, DimColor, true).rectTransform;
            dimRight = CreateImage("DimRight", root, DimColor, true).rectTransform;

            borderTop = CreateImage("BorderTop", root, BorderColor, false);
            borderBottom = CreateImage("BorderBottom", root, BorderColor, false);
            borderLeft = CreateImage("BorderLeft", root, BorderColor, false);
            borderRight = CreateImage("BorderRight", root, BorderColor, false);

            // 圆角 / 正圆用的镂空遮罩与描边（默认关，只有对应形状时打开）
            shapeMask = CreateImage("ShapeMask", root, DimColor, true);
            shapeOutline = CreateImage("ShapeOutline", root, BorderColor, false);

            // 洞口点击代理（透明，需要「只让点目标」时打开）
            Image proxy = CreateImage("ClickProxy", root, new Color(1f, 1f, 1f, 0f), true);
            clickProxy = proxy.rectTransform;
            Button proxyButton = clickProxy.gameObject.AddComponent<Button>();
            proxyButton.transition = Selectable.Transition.None;
            proxyButton.onClick.AddListener(HandleTargetClicked);

            // 提示气泡
            tipRoot = CreateRect("Tip", root);
            tipRoot.anchorMin = tipRoot.anchorMax = new Vector2(0.5f, 0f);
            tipRoot.pivot = new Vector2(0.5f, 0f);
            tipRoot.sizeDelta = new Vector2(TipWidth, TipHeight);
            tipRoot.anchoredPosition = new Vector2(0f, 220f);

            Image tipBg = CreateImage("Bg", tipRoot, TipBgColor, false);
            Stretch(tipBg.rectTransform, Vector2.zero, Vector2.zero);

            tipText = CreateText("TipText", tipRoot, font, 30, TextAnchor.MiddleLeft);
            RectTransform textRect = (RectTransform)tipText.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(26f, 16f);
            textRect.offsetMax = new Vector2(-200f, -16f);
            textRect.pivot = new Vector2(0.5f, 0.5f);

            // 跳过按钮
            skipRoot = CreateRect("Skip", tipRoot);
            skipRoot.anchorMin = skipRoot.anchorMax = new Vector2(1f, 0.5f);
            skipRoot.pivot = new Vector2(1f, 0.5f);
            skipRoot.sizeDelta = new Vector2(160f, 68f);
            skipRoot.anchoredPosition = new Vector2(-20f, 0f);

            skipImage = CreateImage("Bg", skipRoot, new Color(0.28f, 0.30f, 0.36f, 0.95f), true);
            Stretch(skipImage.rectTransform, Vector2.zero, Vector2.zero);

            Button skipButton = skipRoot.gameObject.AddComponent<Button>();
            skipButton.targetGraphic = skipImage;
            skipButton.onClick.AddListener(HandleSkipClicked);

            skipText = CreateText("Label", skipRoot, font, 28, TextAnchor.MiddleCenter);
            Stretch((RectTransform)skipText.transform, Vector2.zero, Vector2.zero);
            skipText.text = "跳过";

            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (hasHole)
            {
                if (target == null)
                {
                    // 目标节点被销毁（面板关了）：退回全屏遮罩，别让洞挂在空气里
                    hasHole = false;
                    LayoutFull();
                }
                else
                {
                    LayoutHole();
                }
            }

            // 描边呼吸：让玩家一眼看到"该点这里"
            Color color = BorderColor;
            color.a = 0.62f + Mathf.PingPong(Time.unscaledTime * 0.9f, 0.38f);
            if (borderTop != null) borderTop.color = color;
            if (borderBottom != null) borderBottom.color = color;
            if (borderLeft != null) borderLeft.color = color;
            if (borderRight != null) borderRight.color = color;
            if (shapeOutline != null) shapeOutline.color = color;
        }

        /// <summary>按目标节点的屏幕矩形摆好遮罩；圆角/正圆时再叠一张生成的镂空贴图。</summary>
        private void LayoutHole()
        {
            float halfWidth = root.rect.width * 0.5f;
            float halfHeight = root.rect.height * 0.5f;

            Vector3[] corners = new Vector3[4];
            target.GetWorldCorners(corners);

            // 4 个角取 min/max：不能假设 corners[0] 是左下、corners[2] 是右上
            // （节点有旋转/镜像时那两个角在世界坐标里未必是最小/最大的）
            Vector2 min = root.InverseTransformPoint(corners[0]);
            Vector2 max = min;
            for (int i = 1; i < 4; i++)
            {
                Vector2 point = root.InverseTransformPoint(corners[i]);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            float x0 = Mathf.Clamp(min.x - HolePadding, -halfWidth, halfWidth);
            float x1 = Mathf.Clamp(max.x + HolePadding, -halfWidth, halfWidth);
            float y0 = Mathf.Clamp(min.y - HolePadding, -halfHeight, halfHeight);
            float y1 = Mathf.Clamp(max.y + HolePadding, -halfHeight, halfHeight);

            if (x1 <= x0 || y1 <= y0)
            {
                LayoutFull();
                hasHole = false;
                return;
            }

            // 正圆：先把洞归一成正方形（取短边居中），否则生成的圆会被拉成椭圆
            if (shape == GuideHoleShape.Circle)
            {
                float side = Mathf.Min(x1 - x0, y1 - y0);
                float centerX = (x0 + x1) * 0.5f;
                float centerY = (y0 + y1) * 0.5f;

                x0 = Mathf.Clamp(centerX - side * 0.5f, -halfWidth, halfWidth);
                x1 = Mathf.Clamp(centerX + side * 0.5f, -halfWidth, halfWidth);
                y0 = Mathf.Clamp(centerY - side * 0.5f, -halfHeight, halfHeight);
                y1 = Mathf.Clamp(centerY + side * 0.5f, -halfHeight, halfHeight);
            }

            // 洞外侧的 4 块矩形遮罩（上下通栏 + 左右窄条）
            SetRect(dimTop, -halfWidth, y1, halfWidth, halfHeight);
            SetRect(dimBottom, -halfWidth, -halfHeight, halfWidth, y0);
            SetRect(dimLeft, -halfWidth, y0, x0, y1);
            SetRect(dimRight, x1, y0, halfWidth, y1);

            bool shaped = shape == GuideHoleShape.RoundRect || shape == GuideHoleShape.Circle;
            float width = x1 - x0;
            float height = y1 - y0;

            // 矩形 / 圆角矩形的直角描边（正圆/圆角用生成的环形描边，所以这里关掉）
            SetActive(borderTop.rectTransform, !shaped);
            SetActive(borderBottom.rectTransform, !shaped);
            SetActive(borderLeft.rectTransform, !shaped);
            SetActive(borderRight.rectTransform, !shaped);
            if (!shaped)
            {
                SetRect(borderTop.rectTransform, x0, y1 - BorderThickness, x1, y1);
                SetRect(borderBottom.rectTransform, x0, y0, x1, y0 + BorderThickness);
                SetRect(borderLeft.rectTransform, x0, y0, x0 + BorderThickness, y1);
                SetRect(borderRight.rectTransform, x1 - BorderThickness, y0, x1, y1);
            }

            SetActive(shapeMask.rectTransform, shaped);
            SetActive(shapeOutline.rectTransform, shaped);

            if (shaped)
            {
                int radius = shape == GuideHoleShape.Circle
                    ? Mathf.RoundToInt(Mathf.Min(width, height) * 0.5f)
                    : Mathf.Max(0, shapeRadius);

                SetRect(shapeMask.rectTransform, x0, y0, x1, y1);
                SetRect(shapeOutline.rectTransform, x0, y0, x1, y1);

                shapeMask.sprite = GetShapeSprite(shape, width, height, radius, false);
                shapeOutline.sprite = GetShapeSprite(shape, width, height, radius, true);
                shapeMask.color = DimColor;
            }

            if (clickProxy.gameObject.activeSelf)
                SetRect(clickProxy, x0, y0, x1, y1);

            // 洞在上半屏 → 气泡放下半屏（反之亦然），别挡住要点的目标
            PlaceTip((y0 + y1) * 0.5f > 0f);
        }

        /// <summary>全屏遮罩（无目标 / 目标丢失 / 纯对话步）。</summary>
        private void LayoutFull()
        {
            float halfWidth = root.rect.width * 0.5f;
            float halfHeight = root.rect.height * 0.5f;

            SetRect(dimTop, -halfWidth, -halfHeight, halfWidth, halfHeight);
            SetRect(dimBottom, 0f, 0f, 0f, 0f);
            SetRect(dimLeft, 0f, 0f, 0f, 0f);
            SetRect(dimRight, 0f, 0f, 0f, 0f);

            SetRect(borderTop.rectTransform, 0f, 0f, 0f, 0f);
            SetRect(borderBottom.rectTransform, 0f, 0f, 0f, 0f);
            SetRect(borderLeft.rectTransform, 0f, 0f, 0f, 0f);
            SetRect(borderRight.rectTransform, 0f, 0f, 0f, 0f);
            SetActive(borderTop.rectTransform, false);
            SetActive(borderBottom.rectTransform, false);
            SetActive(borderLeft.rectTransform, false);
            SetActive(borderRight.rectTransform, false);

            SetActive(shapeMask.rectTransform, false);
            SetActive(shapeOutline.rectTransform, false);

            if (clickProxy.gameObject.activeSelf)
                SetRect(clickProxy, -halfWidth, -halfHeight, halfWidth, halfHeight);

            // 全屏（纯对话）步：气泡放底部，别压住界面标题
            PlaceTip(true);
        }

        /// <summary>提示气泡贴底还是贴顶。贴底时稍微抬高一点，避免和小界面的底部提示重叠。</summary>
        private void PlaceTip(bool bottom)
        {
            if (bottom)
            {
                tipRoot.anchorMin = tipRoot.anchorMax = new Vector2(0.5f, 0f);
                tipRoot.pivot = new Vector2(0.5f, 0f);
                tipRoot.anchoredPosition = new Vector2(0f, 220f);
            }
            else
            {
                tipRoot.anchorMin = tipRoot.anchorMax = new Vector2(0.5f, 1f);
                tipRoot.pivot = new Vector2(0.5f, 1f);
                tipRoot.anchoredPosition = new Vector2(0f, -140f);
            }
        }

        // ---------------- 形状贴图（运行时生成，不依赖美术/不使用 shader） ----------------

        /// <summary>
        /// 生成「形状内部透明、外部不透明」的 alpha 贴图（outline=true 时只留边界内缩的一圈色带）。
        /// 形状用 SDF（圆角矩形）算：圆形就是「正方形 + 角半径 = 半边长」的特例。
        /// </summary>
        private Sprite GetShapeSprite(GuideHoleShape holeShape, float width, float height, int radius, bool outline)
        {
            float scale = 1f;
            float longest = Mathf.Max(width, height);
            if (longest > MaxShapeTextureSize)
                scale = MaxShapeTextureSize / longest;

            int textureWidth = Mathf.Max(8, Mathf.RoundToInt(width * scale));
            int textureHeight = Mathf.Max(8, Mathf.RoundToInt(height * scale));
            int textureRadius = Mathf.Max(0, Mathf.RoundToInt(radius * scale));

            string key = holeShape + "_" + textureWidth + "_" + textureHeight + "_" + textureRadius + (outline ? "_ring" : "_mask");
            Sprite cached;
            if (shapeSprites.TryGetValue(key, out cached) && cached != null)
                return cached;

            Texture2D texture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color32[] pixels = new Color32[textureWidth * textureHeight];

            float halfWidth = width * 0.5f;
            float halfHeight = height * 0.5f;
            float corner = Mathf.Clamp(textureRadius / scale, 0f, Mathf.Min(halfWidth, halfHeight));
            float feather = Mathf.Max(1f, 1f / scale);

            for (int y = 0; y < textureHeight; y++)
            {
                float py = (y + 0.5f) / scale - halfHeight;
                for (int x = 0; x < textureWidth; x++)
                {
                    float px = (x + 0.5f) / scale - halfWidth;

                    float dx = Mathf.Abs(px) - (halfWidth - corner);
                    float dy = Mathf.Abs(py) - (halfHeight - corner);
                    float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                    float inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
                    float distance = outside + inside - corner;   // < 0 = 在形状内部

                    float alpha;
                    if (outline)
                    {
                        // 边界内缩厚度 BorderThickness 的一圈：distance 落在 [-t, 0] 内
                        float band = Mathf.Clamp01(1f - Mathf.Abs(distance + BorderThickness * 0.5f) / (BorderThickness * 0.5f));
                        alpha = band;
                    }
                    else
                    {
                        // 内部透明、外部不透明（羽化 1~2 像素，避免锯齿）
                        alpha = Mathf.Clamp01((distance / feather) + 0.5f);
                    }

                    pixels[y * textureWidth + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, textureWidth, textureHeight), new Vector2(0.5f, 0.5f), 1f);
            shapeSprites[key] = sprite;
            return sprite;
        }

        private void HandleTargetClicked()
        {
            Action callback = onTargetClicked;
            if (callback != null) callback();
        }

        private void HandleSkipClicked()
        {
            Action callback = onSkipClicked;
            if (callback != null) callback();
        }

        // ---------------- 构件工具 ----------------

        private RectTransform CreateRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private Image CreateImage(string name, Transform parent, Color color, bool raycastTarget)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycastTarget;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size, TextAnchor anchor)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            Text text = go.AddComponent<Text>();
            text.font = font != null ? font : BuiltinFont();
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            return text;
        }

        private static void Stretch(RectTransform rect, Vector2 minOffset, Vector2 maxOffset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = minOffset;
            rect.offsetMax = maxOffset;
        }

        /// <summary>用「中心锚点 + 局部坐标矩形」摆一块 UI（坐标是遮罩本体的局部坐标）。</summary>
        private static void SetRect(RectTransform rect, float x0, float y0, float x1, float y1)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            float width = x1 - x0;
            float height = y1 - y0;
            rect.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
            rect.anchoredPosition = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
        }

        private static void SetActive(RectTransform rect, bool active)
        {
            if (rect != null && rect.gameObject.activeSelf != active)
                rect.gameObject.SetActive(active);
        }

        private static Font BuiltinFont()
        {
            try
            {
                Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (font != null) return font;
            }
            catch (Exception)
            {
                // Unity 2021 之后内置字体名换成 LegacyRuntime.ttf，下面再试
            }

            try
            {
                return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
