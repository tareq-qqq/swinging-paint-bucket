using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// =====================================================================================
//  UIBuilder — tiny helper that builds the whole UI in CODE (uGUI), no prefabs/scene wiring.
// =====================================================================================
//  So the setup screen + runtime HUD are created at runtime from C#; the user only drops ONE
//  SimulationBootstrapper in the scene. Uses Unity's layout groups so we never hand-place rects.
//  Each "row" (slider / toggle / dropdown / colour / vector3) binds a label + control to a
//  get/set pair, so SetupUI can wire a field with one line. Built-in render pipeline, uGUI.
// =====================================================================================
public static class UIBuilder
{
    // Simple dark theme.
    public static readonly Color Bg = new Color(0.12f, 0.12f, 0.14f, 0.96f);
    public static readonly Color Panel = new Color(0.17f, 0.17f, 0.20f, 1f);
    public static readonly Color Header = new Color(0.24f, 0.30f, 0.42f, 1f);
    public static readonly Color Accent = new Color(0.30f, 0.55f, 0.95f, 1f);
    public static readonly Color Field = new Color(0.22f, 0.22f, 0.26f, 1f);
    public static readonly Color Text = new Color(0.90f, 0.91f, 0.94f, 1f);

    static Font s_font;
    static Font Fnt =>
        s_font != null
            ? s_font
            : (
                s_font =
                    Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf")
            );

    // --- Canvas + EventSystem (needed once for any UI to receive input) ---
    public static Canvas CreateCanvas(string name, int sortOrder)
    {
        if (FindEventSystem() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem));
            es.AddComponent<StandaloneInputModule>();
        }

        var go = new GameObject(
            name,
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortOrder;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        return canvas;
    }

    static EventSystem FindEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<EventSystem>();
#else
        return UnityEngine.Object.FindObjectOfType<EventSystem>();
#endif
    }

    // --- Basic building blocks ---
    static RectTransform Rect(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null)
            rt = go.AddComponent<RectTransform>();
        return rt;
    }

    public static Image Box(Transform parent, Color color)
    {
        var go = new GameObject("Box", typeof(RectTransform), typeof(Image));
        Rect(go).SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        return img;
    }

    // A full-screen stretched panel with a vertical layout, used as a column root.
    public static RectTransform Column(Transform parent, Color? bg, RectOffset pad, float spacing)
    {
        var go = new GameObject("Column", typeof(RectTransform));
        var rt = Rect(go);
        rt.SetParent(parent, false);
        if (bg.HasValue)
            go.AddComponent<Image>().color = bg.Value;
        var v = go.AddComponent<VerticalLayoutGroup>();
        v.padding = pad ?? new RectOffset(8, 8, 8, 8);
        v.spacing = spacing;
        v.childForceExpandHeight = false;
        v.childForceExpandWidth = true;
        v.childControlHeight = true;
        v.childControlWidth = true;
        return rt;
    }

    public static Text Label(
        Transform parent,
        string text,
        int size,
        TextAnchor anchor,
        Color? col = null
    )
    {
        var go = new GameObject("Label", typeof(RectTransform));
        Rect(go).SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = Fnt;
        t.text = text;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = col ?? Text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    public static Text SectionHeader(Transform parent, string title)
    {
        var bar = Box(parent, Header);
        bar.gameObject.AddComponent<LayoutElement>().minHeight = 40;
        var t = Label(bar.transform, title.ToUpper(), 19, TextAnchor.MiddleLeft);
        StretchWithPad(t.rectTransform, 12);
        return t;
    }

    public static Button Button(
        Transform parent,
        string label,
        Color color,
        Action onClick,
        int minHeight = 40
    )
    {
        var img = Box(parent, color);
        img.gameObject.AddComponent<LayoutElement>().minHeight = minHeight;
        var btn = img.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var t = Label(img.transform, label, 16, TextAnchor.MiddleCenter);
        StretchWithPad(t.rectTransform, 6);
        if (onClick != null)
            btn.onClick.AddListener(() => onClick());
        return btn;
    }

    static void StretchWithPad(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    // A horizontal "label : control" row with a fixed height.
    static RectTransform Row(
        Transform parent,
        out RectTransform right,
        string label,
        float labelWidth = 300f
    )
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(Image));
        var rt = Rect(go);
        rt.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f); // faint stripe + catches scroll drags
        go.AddComponent<LayoutElement>().minHeight = 34;
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 10;
        h.padding = new RectOffset(10, 10, 2, 2);
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childAlignment = TextAnchor.MiddleLeft;

        var lbl = Label(rt, label, 16, TextAnchor.MiddleLeft);
        lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = labelWidth;

        var r = new GameObject("Right", typeof(RectTransform));
        right = Rect(r);
        right.SetParent(rt, false);
        r.AddComponent<LayoutElement>().flexibleWidth = 1;
        var hr = r.AddComponent<HorizontalLayoutGroup>();
        hr.spacing = 6;
        hr.childForceExpandWidth = false; // so a toggle stays a small square instead of filling the row
        hr.childForceExpandHeight = true;
        hr.childControlWidth = true;
        hr.childControlHeight = true;
        hr.childAlignment = TextAnchor.MiddleLeft;
        return rt;
    }

    // --- Bound controls ---
    public static void SliderRow(
        Transform parent,
        string label,
        float min,
        float max,
        bool integer,
        Func<float> get,
        Action<float> set
    )
    {
        Row(parent, out var right, label);

        var valText = Label(right, "", 14, TextAnchor.MiddleRight);
        valText.gameObject.AddComponent<LayoutElement>().preferredWidth = 70;

        var sGo = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        Rect(sGo).SetParent(right, false);
        sGo.AddComponent<LayoutElement>().flexibleWidth = 1;
        var slider = sGo.GetComponent<Slider>();

        var bg = Box(sGo.transform, Field);
        StretchWithPad(bg.rectTransform, 0);
        bg.rectTransform.anchorMin = new Vector2(0, 0.35f);
        bg.rectTransform.anchorMax = new Vector2(1, 0.65f);
        bg.rectTransform.offsetMin = Vector2.zero;
        bg.rectTransform.offsetMax = Vector2.zero;

        var fillArea = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        var fillRt = Rect(fillArea);
        fillRt.SetParent(sGo.transform, false);
        fillRt.anchorMin = new Vector2(0, 0.35f);
        fillRt.anchorMax = new Vector2(1, 0.65f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;
        fillArea.GetComponent<Image>().color = Accent;

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        var hRt = Rect(handle);
        hRt.SetParent(sGo.transform, false);
        hRt.sizeDelta = new Vector2(14, 22);
        handle.GetComponent<Image>().color = Text;

        slider.fillRect = fillRt;
        slider.handleRect = hRt;
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = integer;
        slider.value = Mathf.Clamp(get(), min, max);
        valText.text = integer
            ? Mathf.RoundToInt(slider.value).ToString()
            : slider.value.ToString("0.###");
        slider.onValueChanged.AddListener(v =>
        {
            set(v);
            valText.text = integer ? Mathf.RoundToInt(v).ToString() : v.ToString("0.###");
        });
    }

    public static void ToggleRow(Transform parent, string label, Func<bool> get, Action<bool> set)
    {
        Row(parent, out var right, label);

        // Fixed-width holder (layout-controlled) with the actual checkbox as a centred 24x24 square,
        // so the toggle is a small tidy box rather than a full-width bar.
        var holder = new GameObject("Chk", typeof(RectTransform));
        Rect(holder).SetParent(right, false);
        var hle = holder.AddComponent<LayoutElement>();
        hle.preferredWidth = 28;
        hle.flexibleWidth = 0;

        var box = new GameObject("Box", typeof(RectTransform), typeof(Image), typeof(Toggle));
        var boxRt = Rect(box);
        boxRt.SetParent(holder.transform, false);
        boxRt.anchorMin = new Vector2(0, 0.5f);
        boxRt.anchorMax = new Vector2(0, 0.5f);
        boxRt.pivot = new Vector2(0, 0.5f);
        boxRt.sizeDelta = new Vector2(24, 24);
        boxRt.anchoredPosition = Vector2.zero;
        var img = box.GetComponent<Image>();
        img.color = Field;
        var toggle = box.GetComponent<Toggle>();
        var check = Box(box.transform, Accent);
        StretchWithPad(check.rectTransform, 5);
        toggle.targetGraphic = img;
        toggle.graphic = check;
        toggle.isOn = get();
        toggle.onValueChanged.AddListener(v => set(v));

        var spacer = new GameObject("Spacer", typeof(RectTransform));
        Rect(spacer).SetParent(right, false);
        spacer.AddComponent<LayoutElement>().flexibleWidth = 1;
    }

    public static void DropdownRow(
        Transform parent,
        string label,
        string[] options,
        Func<int> get,
        Action<int> set
    )
    {
        Row(parent, out var right, label);
        var ddGo = new GameObject("Dropdown", typeof(RectTransform), typeof(Image));
        Rect(ddGo).SetParent(right, false);
        ddGo.AddComponent<LayoutElement>().flexibleWidth = 1;
        ddGo.GetComponent<Image>().color = Field;
        var dd = ddGo.AddComponent<Dropdown>();

        var lbl = Label(ddGo.transform, "", 14, TextAnchor.MiddleLeft);
        StretchWithPad(lbl.rectTransform, 8);
        dd.captionText = lbl;

        // Template (required for the popup list).
        var template = new GameObject(
            "Template",
            typeof(RectTransform),
            typeof(Image),
            typeof(ScrollRect)
        );
        var tRt = Rect(template);
        tRt.SetParent(ddGo.transform, false);
        tRt.anchorMin = new Vector2(0, 0);
        tRt.anchorMax = new Vector2(1, 0);
        tRt.pivot = new Vector2(0.5f, 1);
        tRt.sizeDelta = new Vector2(0, 150);
        template.GetComponent<Image>().color = Panel;
        template.SetActive(false);

        var viewport = new GameObject(
            "Viewport",
            typeof(RectTransform),
            typeof(Image),
            typeof(Mask)
        );
        var vpRt = Rect(viewport);
        vpRt.SetParent(template.transform, false);
        vpRt.anchorMin = Vector2.zero;
        vpRt.anchorMax = Vector2.one;
        vpRt.sizeDelta = Vector2.zero;
        viewport.GetComponent<Mask>().showMaskGraphic = false;

        var content = new GameObject("Content", typeof(RectTransform));
        var cRt = Rect(content);
        cRt.SetParent(viewport.transform, false);
        cRt.anchorMin = new Vector2(0, 1);
        cRt.anchorMax = new Vector2(1, 1);
        cRt.pivot = new Vector2(0.5f, 1);
        cRt.sizeDelta = new Vector2(0, 28);

        var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
        var iRt = Rect(item);
        iRt.SetParent(content.transform, false);
        iRt.anchorMin = new Vector2(0, 0.5f);
        iRt.anchorMax = new Vector2(1, 0.5f);
        iRt.sizeDelta = new Vector2(0, 28);
        var itemBg = Box(item.transform, Field);
        StretchWithPad(itemBg.rectTransform, 0);
        var itemLbl = Label(item.transform, "Option", 14, TextAnchor.MiddleLeft);
        StretchWithPad(itemLbl.rectTransform, 8);
        var itemToggle = item.GetComponent<Toggle>();
        itemToggle.targetGraphic = itemBg;

        var sr = template.GetComponent<ScrollRect>();
        sr.content = cRt;
        sr.viewport = vpRt;
        sr.horizontal = false;

        dd.template = tRt;
        dd.itemText = itemLbl;

        dd.options.Clear();
        foreach (var o in options)
            dd.options.Add(new Dropdown.OptionData(o));
        dd.value = Mathf.Clamp(get(), 0, options.Length - 1);
        dd.RefreshShownValue();
        dd.onValueChanged.AddListener(v => set(v));
    }

    public static void ColorRow(Transform parent, string label, Func<Color> get, Action<Color> set)
    {
        Row(parent, out var right, label, 200f);
        Color c = get();

        var swatch = Box(right, c);
        var sle = swatch.gameObject.AddComponent<LayoutElement>();
        sle.preferredWidth = 40;
        sle.flexibleWidth = 0;

        Action refresh = () => swatch.color = get();
        Channel(
            right,
            "R",
            () => get().r,
            v =>
            {
                var x = get();
                x.r = v;
                set(x);
                refresh();
            }
        );
        Channel(
            right,
            "G",
            () => get().g,
            v =>
            {
                var x = get();
                x.g = v;
                set(x);
                refresh();
            }
        );
        Channel(
            right,
            "B",
            () => get().b,
            v =>
            {
                var x = get();
                x.b = v;
                set(x);
                refresh();
            }
        );
    }

    // A tiny labelled 0..1 slider used by ColorRow.
    static void Channel(Transform parent, string label, Func<float> get, Action<float> set)
    {
        var wrap = new GameObject("Ch", typeof(RectTransform));
        Rect(wrap).SetParent(parent, false);
        wrap.AddComponent<LayoutElement>().flexibleWidth = 1;
        var h = wrap.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 3;
        h.childControlWidth = true;
        h.childControlHeight = true;
        var l = Label(wrap.transform, label, 13, TextAnchor.MiddleCenter);
        l.gameObject.AddComponent<LayoutElement>().preferredWidth = 14;

        var sGo = new GameObject("S", typeof(RectTransform), typeof(Slider));
        Rect(sGo).SetParent(wrap.transform, false);
        sGo.AddComponent<LayoutElement>().flexibleWidth = 1;
        var slider = sGo.GetComponent<Slider>();
        var bg = Box(sGo.transform, Field);
        bg.rectTransform.anchorMin = new Vector2(0, 0.3f);
        bg.rectTransform.anchorMax = new Vector2(1, 0.7f);
        bg.rectTransform.offsetMin = Vector2.zero;
        bg.rectTransform.offsetMax = Vector2.zero;
        var handle = new GameObject("H", typeof(RectTransform), typeof(Image));
        var hRt = Rect(handle);
        hRt.SetParent(sGo.transform, false);
        hRt.sizeDelta = new Vector2(10, 18);
        handle.GetComponent<Image>().color = Text;
        slider.handleRect = hRt;
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.minValue = 0;
        slider.maxValue = 1;
        slider.value = get();
        slider.onValueChanged.AddListener(v => set(v));
    }

    public static void Vector3Row(
        Transform parent,
        string label,
        Func<Vector3> get,
        Action<Vector3> set
    )
    {
        Row(parent, out var right, label, 200f);
        Axis(
            right,
            "X",
            () => get().x,
            v =>
            {
                var p = get();
                p.x = v;
                set(p);
            }
        );
        Axis(
            right,
            "Y",
            () => get().y,
            v =>
            {
                var p = get();
                p.y = v;
                set(p);
            }
        );
        Axis(
            right,
            "Z",
            () => get().z,
            v =>
            {
                var p = get();
                p.z = v;
                set(p);
            }
        );
    }

    static void Axis(Transform parent, string axis, Func<float> get, Action<float> set)
    {
        var wrap = new GameObject("Axis", typeof(RectTransform));
        Rect(wrap).SetParent(parent, false);
        wrap.AddComponent<LayoutElement>().flexibleWidth = 1;
        var h = wrap.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 3;
        h.childControlWidth = true;
        h.childControlHeight = true;
        var l = Label(wrap.transform, axis, 13, TextAnchor.MiddleCenter);
        l.gameObject.AddComponent<LayoutElement>().preferredWidth = 14;

        var fGo = new GameObject("Field", typeof(RectTransform), typeof(Image), typeof(InputField));
        Rect(fGo).SetParent(wrap.transform, false);
        fGo.AddComponent<LayoutElement>().flexibleWidth = 1;
        fGo.GetComponent<Image>().color = Field;
        var input = fGo.GetComponent<InputField>();
        var txt = Label(fGo.transform, "", 14, TextAnchor.MiddleLeft);
        StretchWithPad(txt.rectTransform, 6);
        input.textComponent = txt;
        input.contentType = InputField.ContentType.DecimalNumber;
        input.text = get().ToString("0.###");
        input.onEndEdit.AddListener(s =>
        {
            if (float.TryParse(s, out var v))
                set(v);
        });
    }

    // Stretch a RectTransform to fill its parent, with optional per-side insets (l, r, b, t).
    public static void Fill(RectTransform rt, float l = 0, float r = 0, float b = 0, float t = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(l, b);
        rt.offsetMax = new Vector2(-r, -t);
    }

    // Centred vertical region: fixed width, full height minus top/bottom insets. Keeps rows readable
    // instead of stretched edge-to-edge on a wide screen.
    public static void CenterFill(RectTransform rt, float width, float bottom, float top)
    {
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, -(bottom + top));
        rt.anchoredPosition = new Vector2(0f, (bottom - top) * 0.5f);
    }

    // Anchor to a horizontal band at the top (height h, from the top) or bottom (fromBottom).
    public static void Band(RectTransform rt, float height, bool top, float inset = 24)
    {
        rt.anchorMin = new Vector2(0, top ? 1 : 0);
        rt.anchorMax = new Vector2(1, top ? 1 : 0);
        rt.pivot = new Vector2(0.5f, top ? 1 : 0);
        rt.sizeDelta = new Vector2(-2 * inset, height);
        rt.anchoredPosition = new Vector2(0, top ? -inset : inset);
    }

    // A scroll view that FILLS its parent; add rows to the returned Content. Uses RectMask2D
    // (reliable clipping) and a ContentSizeFitter so the content grows with the rows.
    public static RectTransform ScrollColumn(Transform parent)
    {
        var srGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
        var srRt = Rect(srGo);
        srRt.SetParent(parent, false);
        Fill(srRt);
        var sr = srGo.GetComponent<ScrollRect>();
        sr.horizontal = false;
        sr.scrollSensitivity = 24;
        sr.movementType = ScrollRect.MovementType.Clamped;

        var vp = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        var vpRt = Rect(vp);
        vpRt.SetParent(srGo.transform, false);
        Fill(vpRt);

        var content = new GameObject("Content", typeof(RectTransform));
        var cRt = Rect(content);
        cRt.SetParent(vp.transform, false);
        cRt.anchorMin = new Vector2(0, 1);
        cRt.anchorMax = new Vector2(1, 1);
        cRt.pivot = new Vector2(0.5f, 1);
        cRt.sizeDelta = new Vector2(0, 0);
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 4;
        vlg.padding = new RectOffset(10, 10, 10, 10);
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true;
        vlg.childControlWidth = true;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter
            .FitMode
            .PreferredSize;

        sr.viewport = vpRt;
        sr.content = cRt;
        return cRt;
    }
}
