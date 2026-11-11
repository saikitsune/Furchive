/** Validate the complete native runtime qualification receipt, not only process exit.
 * @param {any} result
 */
export function validateQualification(result) {
  const images=['still.jpg','still.png','animation.gif','animation.apng','still.webp'];
  const videos=['vp8.webm','vp9.webm','h264.mp4'];
  if(typeof result?.engine!=='string')throw new Error('missing native engine identity');
  for(const name of images){const item=result.images?.find(/** @param {any} x */x=>x.name===name);if(!item?.ok||item.width<=0||item.height<=0)throw new Error(`image decode failed: ${name}`);}
  for(const name of videos){const item=result.videos?.find(/** @param {any} x */x=>x.name===name);if(!item?.ok||!item.played||!item.seeked||item.time<0.45)throw new Error(`video playback/seek failed: ${name}`);}
  const g=result.gallery;
  if(g?.total!==10000||g.mounted<1||g.mounted>80||g.start<1||g.scrollTop<1000)throw new Error('native gallery virtualization evidence failed');
  return true;
}
