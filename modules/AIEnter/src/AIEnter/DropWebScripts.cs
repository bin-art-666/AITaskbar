using System.Text.Json;

namespace AIEnter;

internal static class DropWebScripts
{
    internal static string FileInput(string[] files)
    {
        string extensions = JsonSerializer.Serialize(files.Select(p => Path.GetExtension(p).ToLowerInvariant()).ToArray());
        return $$"""
        (()=>{
          const visible=e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden';
          const editor=[...document.querySelectorAll('textarea:not([disabled]):not([readonly]),[contenteditable=true][role=textbox],[contenteditable=true]')].find(visible);
          if(!editor)return null;
          const extensions={{extensions}}, images=['.png','.jpg','.jpeg','.webp','.gif','.bmp','.avif','.heic','.heif','.tif','.tiff','.svg'];
          const mime={'.pdf':'application/pdf','.txt':'text/plain','.csv':'text/csv','.json':'application/json','.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.webp':'image/webp','.gif':'image/gif','.doc':'application/msword','.docx':'application/vnd.openxmlformats-officedocument.wordprocessingml.document','.xls':'application/vnd.ms-excel','.xlsx':'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet','.ppt':'application/vnd.ms-powerpoint','.pptx':'application/vnd.openxmlformats-officedocument.presentationml.presentation','.md':'text/markdown','.zip':'application/zip'};
          const distance=e=>{let a=e;for(let d=0;a&&a!==document.body;a=a.parentElement,d++)if(a.contains(editor))return d;return 1000;};
          const candidates=[...document.querySelectorAll('input[type=file]:not([disabled])')].map(e=>{
            if(e.closest('[inert]'))return {e,score:-1};
            if(extensions.length>1&&!e.multiple)return {e,score:-1};
            const accept=e.accept.toLowerCase().split(',').map(s=>s.trim()).filter(Boolean);
          const accepts=ext=>accept.length===0||accept.some(a=>a==='*/*'||a===ext||a===mime[ext]||a===mime[ext]?.split('/')[0]+'/*'||a==='image/*'&&images.includes(ext));
            if(!extensions.every(accepts))return {e,score:-1};
            return {e,score:(accept.length?30:10)+(e.multiple?1:0)+(e.closest('form')?.contains(editor)?5:0)+Math.max(0,20-distance(e)*2)-(e.hasAttribute('capture')?30:0)};
          }).filter(c=>c.score>=0).sort((a,b)=>b.score-a.score);
          if(!candidates.length||candidates.length>1&&candidates[0].score===candidates[1].score)return null;
          return candidates[0].e;
        })()
        """;
    }
    internal const string AttachmentSnapshot = "[...document.images].map(e=>e.currentSrc||e.src).filter(Boolean)";
    internal static string AttachmentReceipt(string[] names, string[]? beforeImages = null, double[]? ratios = null, bool allowNamed = true) => $$"""
        (()=>{
          const names={{JsonSerializer.Serialize(names)}}, before=new Set({{JsonSerializer.Serialize(beforeImages ?? [])}}), ratios={{JsonSerializer.Serialize(ratios ?? [])}};
          const visible=e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden';
          const clean=e=>!importError(e);
          const importError=e=>/上传失败|上传中|正在上传|不支持|超出|uploading|failed|unsupported/i.test((e.closest('[role=listitem]')??e.parentElement)?.innerText??'');
          const nodes=[...document.querySelectorAll('body *')].filter(e=>visible(e)&&!e.matches('input,textarea,script,style')&&e.children.length===0);
          const images=[...document.images].filter(e=>visible(e)&&e.complete&&e.naturalWidth>=16&&e.naturalHeight>=16&&clean(e)&&!before.has(e.currentSrc||e.src));
          const used=new Set();
          return names.every((name,index)=>{
            if({{(allowNamed ? "true" : "false")}}&&nodes.some(e=>[e.textContent,e.getAttribute('title'),e.getAttribute('aria-label')].filter(Boolean).join(' ').includes(name)&&clean(e)))return true;
            if(!ratios[index])return false;
            const image=images.find(e=>!used.has(e)&&Math.abs(e.naturalWidth/e.naturalHeight/ratios[index]-1)<.03);
            if(!image)return false;used.add(image);return true;
          });
        })()
        """;
    internal const string PrepareAttachment = """
        (()=>{
          if(!/(^|\.)doubao\.com$/.test(location.hostname))return false;
          const visible=e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden';
          const names=e=>[e.getAttribute('aria-label'),e.getAttribute('title'),e.textContent].filter(Boolean).join(' ').trim();
          const controls=[...document.querySelectorAll('button,[role=button]')].filter(visible);
          const matches=controls.filter(e=>/^(上传文件|添加附件|上传附件|附件|Upload file|Attach files?)$/i.test(names(e)));
          if(matches.length!==1)return false;
          matches[0].click();return true;
        })()
        """;
    // Native routing consumes the real drop, so the page must still receive the
    // leave/end lifecycle. Do not synthesize a drop or change site DOM/CSS.
    internal const string EndDrag = """
        (()=>{
          const targets=new Set();
          for(const [x,y] of [[innerWidth/2,innerHeight/2],[1,1],[innerWidth-2,innerHeight-2]]){
            let e=document.elementFromPoint(x,y);
            while(e){targets.add(e);e=e.parentElement;}
          }
          targets.add(document);targets.add(window);
          for(const target of targets){
            for(const type of ['dragleave','dragend'])
              target.dispatchEvent(new DragEvent(type,{bubbles:false,cancelable:true,clientX:-1,clientY:-1,relatedTarget:null}));
          }
          return true;
        })()
        """;
    internal const string FocusEditor = """
        (()=>{
          const e=[...document.querySelectorAll('textarea:not([disabled]):not([readonly]),[contenteditable=true][role=textbox],[contenteditable=true]')]
            .find(e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden');
          if(!e)return null;
          const text=e.value??e.innerText??'';
          e.focus();
          if(e.setSelectionRange)e.setSelectionRange(e.value.length,e.value.length);
          else{const r=document.createRange();r.selectNodeContents(e);r.collapse(false);const s=getSelection();s.removeAllRanges();s.addRange(r);}
          return text.length&&!text.endsWith('\n')?'newline':'empty';
        })()
        """;
}
