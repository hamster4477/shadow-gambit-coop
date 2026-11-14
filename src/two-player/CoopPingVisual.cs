using System;
using UnityEngine;

// Borrow only the native flag sprite. Never clone or place ViewConeMarker:
// that singleton owns local guard-view selection and detection callbacks.
internal sealed class CoopPingVisual : IDisposable
{
    private GameObject root;
    private Material material;
    private readonly Vector3 point;
    private readonly float spriteHeight;
    private readonly SpriteRenderer[] outline=new SpriteRenderer[8];
    private bool disposed;
    private const float ScreenHeight=46f;

    internal static CoopPingVisual Create(Vector3 point,Color color)
    {
        var native=SceneLookup.Find<ViewConeState>()?.VCMarker?.vcMarkerUI;
        var sprite=native?.m_spriteNormal;
        if(sprite==null)throw new InvalidOperationException("Native view-cone flag sprite is not ready.");
        return new CoopPingVisual(sprite,point,color);
    }

    private CoopPingVisual(Sprite sprite,Vector3 position,Color color)
    {
        point=position;spriteHeight=Mathf.Max(.001f,sprite.bounds.size.y);
        try{
            var shader=Shader.Find("Sprites/Default")??Shader.Find("UI/Default");
            if(shader==null)throw new InvalidOperationException("Native flag display shader is unavailable.");
            material=new Material(shader);material.name="CoopPing.Flag.Material";
            root=new GameObject("CoopPing.Flag");
            // No colliders, UI input targets, native game components or save identities.
            root.layer=2;root.hideFlags=HideFlags.DontSaveInEditor|HideFlags.DontSaveInBuild;
            color.a=.95f;
            for(int i=0;i<outline.Length;i++)outline[i]=AddSprite("Outline",sprite,color,20);
            AddSprite("NativeFlag",sprite,Color.white,21);
            Tick();
        }catch{Dispose();throw;}
    }

    private SpriteRenderer AddSprite(string name,Sprite sprite,Color color,int order)
    {
        var child=new GameObject(name);child.layer=root.layer;child.transform.SetParent(root.transform,false);
        child.hideFlags=root.hideFlags;
        var renderer=child.AddComponent<SpriteRenderer>();renderer.sprite=sprite;
        renderer.sharedMaterial=material;renderer.color=color;renderer.sortingOrder=order;
        renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;
        return renderer;
    }

    internal void Tick()
    {
        if(disposed||root==null)return;
        var camera=Camera.main;
        if(camera==null){root.SetActive(false);return;}
        var viewport=camera.WorldToViewportPoint(point);
        bool visible=viewport.z>camera.nearClipPlane;
        root.SetActive(visible);if(!visible)return;
        // Prefer the non-raycast layer; fall back to an actually rendered layer.
        int mask=camera.cullingMask,layer=2;
        if((mask&(1<<layer))==0){layer=0;while(layer<31&&(mask&(1<<layer))==0)layer++;}
        if(root.layer!=layer){root.layer=layer;foreach(var r in root.GetComponentsInChildren<SpriteRenderer>(true))r.gameObject.layer=layer;}
        float viewHeight=camera.orthographic?camera.orthographicSize*2f:2f*viewport.z*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f);
        float scale=viewHeight*ScreenHeight/Mathf.Max(1,camera.pixelHeight)/spriteHeight;
        root.transform.SetPositionAndRotation(point+Vector3.up*.08f,camera.transform.rotation);
        root.transform.localScale=Vector3.one*scale;
        // Offset independent copies, leaving the original flag's texture/material untouched.
        float radius=spriteHeight*1.6f/ScreenHeight;
        for(int i=0;i<outline.Length;i++){
            float angle=i*Mathf.PI/4f;
            outline[i].transform.localPosition=new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,.001f);
        }
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{if(root!=null)UnityEngine.Object.Destroy(root);}
        finally{root=null;if(material!=null)UnityEngine.Object.Destroy(material);material=null;}
    }
}
