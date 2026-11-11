/** Return a half-open item range for row-based grid virtualization.
 * @param {number} count @param {number} columns @param {number} scrollTop
 * @param {number} height @param {number} rowHeight @param {number} overscan
 */
export function visibleRange(count, columns, scrollTop, height, rowHeight, overscan=2) {
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const last = Math.ceil((scrollTop + height) / rowHeight) + overscan;
  return { start: Math.min(count, first * columns), end: Math.min(count, last * columns) };
}
