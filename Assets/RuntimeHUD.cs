using UnityEngine;
using UnityEngine.UI;

// =====================================================================================
//  RuntimeHUD — the minimal overlay shown while the simulation runs.
// =====================================================================================
//  A Screenshot button (exports the canvas PNG, same as pressing P) and a Restart button
//  (back to the setup screen). Everything else keeps working untouched — Space kicks the
//  bucket, 1–4 swap the rope material, mouse-drag moves it. Built in code via UIBuilder.
// =====================================================================================
public class RuntimeHUD : MonoBehaviour
{
    public void Build(SimulationBootstrapper boot, PaintCanvas canvas)
    {
        var cv = UIBuilder.CreateCanvas("RuntimeHUD", 90);

        // Top-right button stack.
        var panel = UIBuilder.Column(cv.transform, null, new RectOffset(0, 0, 0, 0), 6);
        panel.anchorMin = new Vector2(1, 1);
        panel.anchorMax = new Vector2(1, 1);
        panel.pivot = new Vector2(1, 1);
        panel.anchoredPosition = new Vector2(-16, -16);
        panel.sizeDelta = new Vector2(200, 100);

        if (canvas != null)
            UIBuilder.Button(panel, "Screenshot", UIBuilder.Accent, () => canvas.ExportPng(), 40);
        UIBuilder.Button(panel, "Restart", UIBuilder.Header, boot.Restart, 40);

        // Bottom-left controls hint.
        var hint = UIBuilder.Label(cv.transform, "Space = kick   |   1-4 = rope material   |   drag the bucket   |   P / Screenshot = save canvas",
            15, TextAnchor.LowerLeft);
        hint.rectTransform.anchorMin = new Vector2(0, 0);
        hint.rectTransform.anchorMax = new Vector2(1, 0);
        hint.rectTransform.pivot = new Vector2(0, 0);
        hint.rectTransform.anchoredPosition = new Vector2(16, 12);
        hint.rectTransform.sizeDelta = new Vector2(-32, 26);
    }
}
