# FastYuv on a resolution change (2026-10-12)

FastYuv converted a new size's frames at the stream's size after scaling the YUV, where OpenCV scales them inside its
one bicubic conversion to BGR, so res_change.ts's second half differed (2 of 7 content cuts). Asked: **fall back on a
size change** (those frames take the core path's exact conversion; in-process) over porting swscale's scaled
conversion into FastYuv, or leaving it informational.
