export type MediaItem = Readonly<{id:string; kind:'image'|'video'; src:string; thumbnail?:string; title:string}>;
// Only our own deeply frozen outputs are eligible; mutable caller inputs never
// become cache keys. Grant signatures are recomputed so revocation is immediate.
const validatedCatalogs=new WeakMap<readonly MediaItem[],string>();

/** Exact URL grants are supplied by the trusted caller, not discovered by this UI.
 * Only bundled absolute paths and the existing tokenized loopback fixture seam are supported.
 * This is a presentation boundary, not a replacement for host CSP/IPC authorization. */
export function createCatalog(input: readonly MediaItem[], grants: readonly string[]): readonly MediaItem[] {
  const grantSignature=JSON.stringify(grants);
  if(validatedCatalogs.get(input)===grantSignature)return input;
  const allowed = new Set(grants);
  const ids = new Set<string>();
  function checkUrl(value:unknown): asserts value is string {
    if(typeof value !== 'string' || value.length > 4096 || /[\\\s%?#]/.test(value) || value.split('/').includes('..') || value.split('/').includes('.')) throw new Error('invalid media URL');
    const bundled = /^\/(?!\/)[a-zA-Z0-9_./-]+$/.test(value);
    const loopback = /^http:\/\/127\.0\.0\.1:[1-9][0-9]{0,4}\/[a-f0-9]{64}\/(?:vp8\.webm|vp9\.webm|h264\.mp4)$/.test(value);
    if(!bundled && !loopback) throw new Error('unsupported media URL');
    if(loopback && Number(new URL(value).port)>65535) throw new Error('invalid media URL port');
    if(!allowed.has(value)) throw new Error('missing exact URL grant');
  }
  const result=input.map(item=>{
    if(typeof item.id !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(item.id) || ids.has(item.id)) throw new Error('invalid or duplicate media ID');
    ids.add(item.id);
    if(item.kind !== 'image' && item.kind !== 'video') throw new Error('invalid media kind');
    if(typeof item.title !== 'string' || item.title.length>1000) throw new Error('invalid title');
    checkUrl(item.src);
    if(item.thumbnail!==undefined) checkUrl(item.thumbnail);
    return Object.freeze({id:item.id,kind:item.kind,src:item.src,title:item.title,...(item.thumbnail?{thumbnail:item.thumbnail}:{})});
  });
  const frozen=Object.freeze(result);validatedCatalogs.set(frozen,grantSignature);return frozen;
}

/** Every navigation owns one release callback. Closing invalidates even already queued events. */
export class LoadScope {
  private generation=0;
  private cleanup: (()=>void)|undefined;
  begin(cleanup:()=>void) {
    this.close();
    const generation=this.generation;
    this.cleanup=cleanup;
    const current=()=>generation===this.generation;
    return {current, commit:(fn:()=>void)=>{if(!current()) return false; fn(); return true;}};
  }
  close() {
    this.generation++;
    const cleanup=this.cleanup; this.cleanup=undefined; cleanup?.();
  }
}
export function releaseVideo(video:Pick<HTMLVideoElement,'pause'|'removeAttribute'|'load'>) {
  video.pause(); video.removeAttribute('src'); video.load();
}
// A post-metadata UI refusal, not a decoder-allocation sandbox: host must budget untrusted input.
export function imageAllowed(width:number,height:number) {
  return Number.isSafeInteger(width) && Number.isSafeInteger(height) && width>0 && height>0 && width<=16384 && height<=16384 && width*height<=32_000_000;
}
