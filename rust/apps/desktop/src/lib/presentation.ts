// Pure UI geometry and selection; no domain or persistence authority.
export const ROW_HEIGHT=184;
const clamp=(n:number,min:number,max:number)=>Math.max(min,Math.min(max,Number.isFinite(n)?n:min));
export function gridWindow(count:number,width:number,scroll:number,height:number,scale=1) {
  if(!Number.isFinite(scale)||scale<.5||scale>2)throw Error('invalid gallery scale');
  const rowHeight=ROW_HEIGHT*scale;
  // Cap mounted cells regardless of tiny tiles and wide windows.
  const viewport=clamp(height,1,736);
  const maxRows=Math.ceil(viewport/rowHeight)+5;
  const columns=Math.floor(clamp(Math.floor(width/(180*scale)),1,Math.min(12,Math.floor(108/maxRows))));
  const rows=Math.ceil(count/columns);
  const scrollTop=clamp(scroll,0,Math.max(0,rows*rowHeight-viewport));
  const first=Math.max(0,Math.floor(scrollTop/rowHeight)-2);
  const last=Math.min(rows,Math.ceil((scrollTop+viewport)/rowHeight)+2);
  return {columns,start:Math.min(count,first*columns),end:Math.min(count,last*columns),height:rows*rowHeight,top:first*rowHeight,scrollTop,rowHeight};
}
export function navigateId(ids:readonly string[],selected:string|null,key:string,columns:number):string|null {
  if(!ids.length) return null;
  const index=Math.max(0,ids.indexOf(selected??''));
  const delta:Record<string,number>={ArrowLeft:-1,ArrowRight:1,ArrowUp:-columns,ArrowDown:columns};
  const next=key==='Home'?0:key==='End'?ids.length-1:index+(delta[key]??0);
  return ids[clamp(next,0,ids.length-1)];
}
export function fitScale(w:number,h:number,vw:number,vh:number) { return Math.min(1,vw/Math.max(1,w),vh/Math.max(1,h)); }
export function clampTransform(w:number,h:number,vw:number,vh:number,scale:number,x:number,y:number) {
  scale=clamp(scale,Math.min(0.05,fitScale(w,h,vw,vh)),8);
  const dx=Math.max(0,(w*scale-vw)/2),dy=Math.max(0,(h*scale-vh)/2);
  return {scale,x:dx?clamp(x,-dx,dx):0,y:dy?clamp(y,-dy,dy):0};
}
