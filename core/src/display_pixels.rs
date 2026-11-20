//! Bounded desktop geometry, rotation, cursor composition and video colors.
//! Kept independent of Windows so the capture math is tested on every host.

pub(crate) const ORIGINAL_PIXEL_BUDGET: u64 = 3840 * 2160;

pub(crate) fn size(width: u32, height: u32, maximum: u32, original: bool) -> Option<(u32, u32)> {
    if width < 2 || height < 2 || width > 16_384 || height > 16_384 || u64::from(width) * u64::from(height) > 67_108_864
    {
        return None;
    }
    if original {
        let edge = u64::from(maximum.clamp(352, 3840));
        let longest = u64::from(width.max(height));
        let (mut w, mut h) = if longest > edge {
            ((u64::from(width) * edge / longest) as u32, (u64::from(height) * edge / longest) as u32)
        } else {
            (width, height)
        };
        let pixels = u64::from(w) * u64::from(h);
        if pixels > ORIGINAL_PIXEL_BUDGET {
            let scale = (ORIGINAL_PIXEL_BUDGET as f64 / pixels as f64).sqrt();
            w = (f64::from(w) * scale) as u32;
            h = (f64::from(h) * scale) as u32;
        }
        return Some((w.max(2), h.max(2)));
    }
    let longest = maximum.clamp(352, 1920) as f64;
    let shortest = (longest * 9.0 / 16.0).floor();
    let (max_width, max_height) = if width >= height { (longest, shortest) } else { (shortest, longest) };
    let scale = 1.0_f64.min(max_width / f64::from(width)).min(max_height / f64::from(height));
    Some((((f64::from(width) * scale) as u32 / 2 * 2).max(2), ((f64::from(height) * scale) as u32 / 2 * 2).max(2)))
}

pub(crate) fn texture_matches(desktop: (u32, u32), rotation: u32, texture: (u32, u32)) -> bool {
    let expected = if rotation == 2 || rotation == 4 { (desktop.1, desktop.0) } else { desktop };
    expected == texture
}

pub(crate) fn buffer_alignment(required: u32) -> Option<u32> {
    // Stream-info alignment is a byte requirement; MFCreateAlignedMemoryBuffer
    // takes a mask. Rounding up also tolerates drivers returning a mask here.
    // Use at least 16-byte alignment for the CPU color planes.
    (required <= 65_536).then(|| required.max(16).next_power_of_two() - 1)
}

#[derive(Clone, Copy)]
struct AxisSample {
    low: usize,
    high: usize,
    weight: u32,
}

/// Reuse interpolation coordinates and output storage across desktop frames.
pub(crate) struct Resizer {
    source: (usize, usize),
    size: (u32, u32),
    columns: Vec<AxisSample>,
    rows: Vec<AxisSample>,
    swapped: bool,
    direct: bool,
}
impl Resizer {
    pub(crate) fn new(source: (usize, usize), size: (u32, u32), rotation: u32) -> Self {
        fn axis(source: usize, output: u32, reverse: bool) -> Vec<AxisSample> {
            (0..output)
                .map(|i| {
                    let value = ((f64::from(i) + 0.5) * source as f64 / f64::from(output) - 0.5)
                        .clamp(0.0, (source - 1) as f64);
                    let value = if reverse { (source - 1) as f64 - value } else { value };
                    let low = value.floor() as usize;
                    AxisSample {
                        low,
                        high: (low + 1).min(source - 1),
                        weight: ((value - low as f64) * 256.0).round() as u32,
                    }
                })
                .collect()
        }
        let swapped = rotation == 2 || rotation == 4;
        let (width, height) = if swapped { (source.1, source.0) } else { source };
        Self {
            source,
            size,
            columns: axis(width, size.0, rotation == 2 || rotation == 3),
            rows: axis(height, size.1, rotation == 3 || rotation == 4),
            swapped,
            direct: rotation <= 1 && source == (size.0 as usize, size.1 as usize),
        }
    }

    pub(crate) fn rgba(&self, source: &[u8], stride: usize, result: &mut Vec<u8>) {
        let width = self.size.0 as usize;
        result.resize(width * self.size.1 as usize * 4, 0);
        if self.direct {
            for (input, output) in source.chunks(stride).zip(result.chunks_exact_mut(width * 4)) {
                for (input, output) in
                    input[..self.source.0 * 4].as_chunks::<4>().0.iter().zip(output.as_chunks_mut::<4>().0)
                {
                    *output = [input[2], input[1], input[0], 255];
                }
            }
            return;
        }
        for (row, output) in self.rows.iter().zip(result.chunks_exact_mut(width * 4)) {
            for (column, pixel) in self.columns.iter().zip(output.as_chunks_mut::<4>().0) {
                let (x, y) = if self.swapped { (row, column) } else { (column, row) };
                let (a, b) = (y.low * stride, y.high * stride);
                let (left, right) = (x.low * 4, x.high * 4);
                for (channel, bgra) in [(0, 2), (1, 1), (2, 0)] {
                    let top = u32::from(source[a + left + bgra]) * (256 - x.weight)
                        + u32::from(source[a + right + bgra]) * x.weight;
                    let bottom = u32::from(source[b + left + bgra]) * (256 - x.weight)
                        + u32::from(source[b + right + bgra]) * x.weight;
                    pixel[channel] = ((top * (256 - y.weight) + bottom * y.weight + 32_768) >> 16) as u8;
                }
                pixel[3] = 255;
            }
        }
    }
}

#[cfg(test)]
fn rgba(source: &[u8], width: usize, height: usize, stride: usize, rotation: u32, out: (u32, u32)) -> Vec<u8> {
    let mut result = vec![];
    Resizer::new((width, height), out, rotation).rgba(source, stride, &mut result);
    result
}

#[cfg(test)]
fn reference_rgba(
    source: &[u8],
    width: usize,
    height: usize,
    stride: usize,
    rotation: u32,
    out: (u32, u32),
) -> Vec<u8> {
    let (ow, oh) = (out.0 as usize, out.1 as usize);
    let (lw, lh) = if rotation == 2 || rotation == 4 { (height, width) } else { (width, height) };
    let mut result = vec![0; ow * oh * 4];
    for y in 0..oh {
        let sy = ((y as f64 + 0.5) * lh as f64 / oh as f64 - 0.5).clamp(0.0, (lh - 1) as f64);
        for x in 0..ow {
            let sx = ((x as f64 + 0.5) * lw as f64 / ow as f64 - 0.5).clamp(0.0, (lw - 1) as f64);
            let (rx, ry) = match rotation {
                2 => (sy, (height - 1) as f64 - sx),
                3 => ((width - 1) as f64 - sx, (height - 1) as f64 - sy),
                4 => ((width - 1) as f64 - sy, sx),
                _ => (sx, sy),
            };
            let (x0, y0) = (rx.floor() as usize, ry.floor() as usize);
            let (x1, y1) = ((x0 + 1).min(width - 1), (y0 + 1).min(height - 1));
            let (fx, fy) = (rx - x0 as f64, ry - y0 as f64);
            for (channel, bgra) in [(0, 2), (1, 1), (2, 0)] {
                let a = source[y0 * stride + x0 * 4 + bgra] as f64 * (1.0 - fx)
                    + source[y0 * stride + x1 * 4 + bgra] as f64 * fx;
                let b = source[y1 * stride + x0 * 4 + bgra] as f64 * (1.0 - fx)
                    + source[y1 * stride + x1 * 4 + bgra] as f64 * fx;
                result[(y * ow + x) * 4 + channel] = (a * (1.0 - fy) + b * fy).round() as u8;
            }
            result[(y * ow + x) * 4 + 3] = 255;
        }
    }
    result
}

pub(crate) fn nv12(rgba: &[u8], width: usize, height: usize) -> Vec<u8> {
    let mut output = vec![];
    nv12_into(rgba, width, height, &mut output);
    output
}

pub(crate) fn nv12_into(rgba: &[u8], width: usize, height: usize, output: &mut Vec<u8>) {
    output.resize(width * height * 3 / 2, 0);
    let clamp = |v: i32| v.clamp(0, 255) as u8;
    // BT.709 video-range (16..=235 Y, 16..=240 CbCr) matching MFVideoTransferMatrix_BT709.
    for y in (0..height).step_by(2) {
        for x in (0..width).step_by(2) {
            let (mut r, mut g, mut b) = (0, 0, 0);
            for dy in 0..2 {
                for dx in 0..2 {
                    let p = ((y + dy) * width + x + dx) * 4;
                    let (pr, pg, pb) = (rgba[p] as i32, rgba[p + 1] as i32, rgba[p + 2] as i32);
                    output[(y + dy) * width + x + dx] = clamp(((47 * pr + 157 * pg + 16 * pb + 128) >> 8) + 16);
                    r += pr;
                    g += pg;
                    b += pb;
                }
            }
            let p = width * height + y / 2 * width + x;
            output[p] = clamp(((-26 * (r / 4) - 86 * (g / 4) + 112 * (b / 4) + 128) >> 8) + 128);
            output[p + 1] = clamp(((112 * (r / 4) - 102 * (g / 4) - 10 * (b / 4) + 128) >> 8) + 128);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct PixelRect {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}

impl PixelRect {
    pub(crate) fn from_bounds(x0: i64, y0: i64, x1: i64, y1: i64, bounds: (u32, u32)) -> Option<Self> {
        let bw = i64::from(bounds.0);
        let bh = i64::from(bounds.1);
        let left = x0.clamp(0, bw);
        let top = y0.clamp(0, bh);
        let right = x1.clamp(0, bw);
        let bottom = y1.clamp(0, bh);
        (right > left && bottom > top).then_some(Self {
            x: left as u32,
            y: top as u32,
            width: (right - left) as u32,
            height: (bottom - top) as u32,
        })
    }

    #[cfg_attr(not(windows), allow(dead_code))]
    pub(crate) fn union(self, other: Self) -> Self {
        let x0 = self.x.min(other.x);
        let y0 = self.y.min(other.y);
        let x1 = (self.x + self.width).max(other.x + other.width);
        let y1 = (self.y + self.height).max(other.y + other.height);
        Self { x: x0, y: y0, width: x1 - x0, height: y1 - y0 }
    }

    pub(crate) fn area(self) -> u64 {
        u64::from(self.width) * u64::from(self.height)
    }

    pub(crate) fn exceeds_patch_threshold(self, full: (u32, u32)) -> bool {
        self.area().saturating_mul(100) > u64::from(full.0).saturating_mul(u64::from(full.1)).saturating_mul(55)
    }
}

/// Maps a DXGI texture rectangle `(left, top, right, bottom)` through monitor
/// rotation and output scaling, padded by 1px on each edge when rescaling so
/// bilinear filter taps are never clipped.
pub(crate) fn transform_texture_rect(
    rect: (i32, i32, i32, i32),
    texture: (u32, u32),
    rotation: u32,
    output: (u32, u32),
) -> Option<PixelRect> {
    if texture.0 == 0 || texture.1 == 0 || output.0 == 0 || output.1 == 0 {
        return None;
    }
    let tw = i64::from(texture.0);
    let th = i64::from(texture.1);
    let l = i64::from(rect.0).clamp(0, tw);
    let t = i64::from(rect.1).clamp(0, th);
    let r = i64::from(rect.2).clamp(0, tw);
    let b = i64::from(rect.3).clamp(0, th);
    if r <= l || b <= t {
        return None;
    }
    let (lx0, ly0, lx1, ly1, lw, lh) = match rotation {
        2 => (th - b, l, th - t, r, th, tw),
        3 => (tw - r, th - b, tw - l, th - t, tw, th),
        4 => (t, tw - r, b, tw - l, th, tw),
        _ => (l, t, r, b, tw, th),
    };
    if lw == i64::from(output.0) && lh == i64::from(output.1) {
        return PixelRect::from_bounds(lx0, ly0, lx1, ly1, output);
    }
    let sx = f64::from(output.0) / lw as f64;
    let sy = f64::from(output.1) / lh as f64;
    let x0 = (lx0 as f64 * sx).floor() as i64 - 1;
    let y0 = (ly0 as f64 * sy).floor() as i64 - 1;
    let x1 = (lx1 as f64 * sx).ceil() as i64 + 1;
    let y1 = (ly1 as f64 * sy).ceil() as i64 + 1;
    PixelRect::from_bounds(x0, y0, x1, y1, output)
}

#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn cursor_rect(
    out: (u32, u32),
    desktop: (u32, u32),
    x: i32,
    y: i32,
    width: usize,
    height: usize,
    kind: u32,
) -> Option<PixelRect> {
    let height = if kind == 1 { height / 2 } else { height };
    if width == 0 || height == 0 || desktop.0 == 0 || desktop.1 == 0 || out.0 == 0 || out.1 == 0 {
        return None;
    }
    let sx = f64::from(out.0) / f64::from(desktop.0);
    let sy = f64::from(out.1) / f64::from(desktop.1);
    let left = (f64::from(x) * sx).floor() as i64;
    let top = (f64::from(y) * sy).floor() as i64;
    let right = ((f64::from(x) + width as f64) * sx).ceil() as i64;
    let bottom = ((f64::from(y) + height as f64) * sy).ceil() as i64;
    PixelRect::from_bounds(left, top, right, bottom, out)
}

pub(crate) fn crop_rgba(image: &[u8], full_width: u32, rect: PixelRect) -> Vec<u8> {
    let full_stride = full_width as usize * 4;
    let row_bytes = rect.width as usize * 4;
    let mut out = Vec::with_capacity(row_bytes * rect.height as usize);
    for row in rect.y..(rect.y + rect.height) {
        let start = row as usize * full_stride + rect.x as usize * 4;
        out.extend_from_slice(&image[start..start + row_bytes]);
    }
    out
}

pub(crate) fn patch_payload(rect: PixelRect, full: (u32, u32), png: &[u8]) -> Option<Vec<u8>> {
    let x = u16::try_from(rect.x).ok()?;
    let y = u16::try_from(rect.y).ok()?;
    let fw = u16::try_from(full.0).ok()?;
    let fh = u16::try_from(full.1).ok()?;
    let mut payload = Vec::with_capacity(8 + png.len());
    for value in [x, y, fw, fh] {
        payload.extend_from_slice(&value.to_le_bytes());
    }
    payload.extend_from_slice(png);
    Some(payload)
}

pub(crate) struct Cursor<'a> {
    pub data: &'a [u8],
    pub width: usize,
    pub height: usize,
    pub pitch: usize,
    pub kind: u32,
    pub x: i32,
    pub y: i32,
}

pub(crate) fn cursor(image: &mut [u8], out: (u32, u32), desktop: (u32, u32), shape: Cursor<'_>) {
    let (ow, oh) = (out.0 as usize, out.1 as usize);
    let height = if shape.kind == 1 { shape.height / 2 } else { shape.height };
    if shape.width == 0
        || height == 0
        || shape.pitch == 0
        || shape.data.len() < shape.pitch.saturating_mul(shape.height)
    {
        return;
    }
    if (shape.kind == 1 && shape.pitch < shape.width.div_ceil(8))
        || (shape.kind != 1 && shape.pitch < shape.width.saturating_mul(4))
    {
        return;
    }
    let (sx, sy) = (ow as f64 / desktop.0 as f64, oh as f64 / desktop.1 as f64);
    let (left, top) = (shape.x as f64 * sx, shape.y as f64 * sy);
    let (right, bottom) = (left + shape.width as f64 * sx, top + height as f64 * sy);
    for y in (top.floor().max(0.0) as usize)..(bottom.ceil().max(0.0) as usize).min(oh) {
        for x in (left.floor().max(0.0) as usize)..(right.ceil().max(0.0) as usize).min(ow) {
            let px = (((x as f64 - left) / sx) as usize).min(shape.width - 1);
            let py = (((y as f64 - top) / sy) as usize).min(height - 1);
            let p = (y * ow + x) * 4;
            if shape.kind == 1 {
                let mask = 0x80 >> (px % 8);
                let and = if shape.data[py * shape.pitch + px / 8] & mask != 0 { 255 } else { 0 };
                let xor = if shape.data[(py + height) * shape.pitch + px / 8] & mask != 0 { 255 } else { 0 };
                for c in 0..3 {
                    image[p + c] = (image[p + c] & and) ^ xor;
                }
            } else {
                let q = py * shape.pitch + px * 4;
                let alpha = shape.data[q + 3] as u32;
                for (c, b) in [(0, 2), (1, 1), (2, 0)] {
                    let value = shape.data[q + b];
                    image[p + c] = if shape.kind == 4 {
                        if alpha == 0 { value } else { image[p + c] ^ value }
                    } else {
                        ((value as u32 * alpha + image[p + c] as u32 * (255 - alpha) + 127) / 255) as u8
                    };
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn resolution_and_portrait_respect_pixel_budget() {
        assert_eq!(size(3840, 2160, 3840, true), Some((3840, 2160)));
        assert_eq!(size(2160, 3840, 3840, true), Some((2160, 3840)));
        assert_eq!(size(5120, 1440, 3840, true), Some((3840, 1080)));
        assert_eq!(size(3000, 2000, 3840, true), Some((3000, 2000)));
        let tall = size(3840, 2400, 3840, true).unwrap();
        assert!(u64::from(tall.0) * u64::from(tall.1) <= ORIGINAL_PIXEL_BUDGET);
        assert_eq!(size(1920, 1200, 1280, false), Some((1152, 720)));
        assert!(size(u32::MAX, 2, 3840, true).is_none());
    }

    #[test]
    fn dirty_rect_transform_covers_changed_pixels_across_rotations_and_scales() {
        for (tw, th) in [(8u32, 6u32), (10, 6)] {
            for rotation in 1..=4 {
                let out = if rotation == 2 || rotation == 4 { (6, 8) } else { (8, 6) };
                for out_size in [out, (4, 4)] {
                    let base = vec![40u8; (tw * th * 4) as usize];
                    let mut modified = base.clone();
                    let rect = (2i32, 1i32, 5i32, 4i32);
                    for y in rect.1..rect.3 {
                        for x in rect.0..rect.2 {
                            let idx = (y as usize * tw as usize + x as usize) * 4;
                            modified[idx..idx + 4].copy_from_slice(&[200, 150, 100, 255]);
                        }
                    }
                    let before = rgba(&base, tw as usize, th as usize, tw as usize * 4, rotation, out_size);
                    let after = rgba(&modified, tw as usize, th as usize, tw as usize * 4, rotation, out_size);
                    let mapped = transform_texture_rect(rect, (tw, th), rotation, out_size).unwrap();
                    for y in 0..out_size.1 {
                        for x in 0..out_size.0 {
                            let idx = (y * out_size.0 + x) as usize * 4;
                            if before[idx..idx + 4] != after[idx..idx + 4] {
                                assert!(
                                    x >= mapped.x
                                        && x < mapped.x + mapped.width
                                        && y >= mapped.y
                                        && y < mapped.y + mapped.height,
                                    "changed pixel ({x},{y}) fell outside {mapped:?} for rot={rotation} out={out_size:?}"
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn patch_payload_and_crop_encode_little_endian_header_and_exact_region() {
        let mut image = vec![0u8; 4 * 4 * 4];
        for y in 0..4u32 {
            for x in 0..4u32 {
                let idx = (y * 4 + x) as usize * 4;
                image[idx..idx + 4].copy_from_slice(&[x as u8, y as u8, 99, 255]);
            }
        }
        let rect = PixelRect { x: 1, y: 2, width: 2, height: 2 };
        let cropped = crop_rgba(&image, 4, rect);
        assert_eq!(cropped, [1, 2, 99, 255, 2, 2, 99, 255, 1, 3, 99, 255, 2, 3, 99, 255]);
        assert!(!rect.exceeds_patch_threshold((4, 4)));
        assert!(PixelRect { x: 0, y: 0, width: 3, height: 3 }.exceeds_patch_threshold((4, 4)));

        let payload = patch_payload(rect, (1920, 1080), &[1, 2, 3]).unwrap();
        assert_eq!(&payload[..8], &[1, 0, 2, 0, 0x80, 0x07, 0x38, 0x04]);
        assert_eq!(&payload[8..], &[1, 2, 3]);
    }
    #[test]
    fn rotated_texture_dimensions_must_match_the_selected_monitor() {
        assert!(texture_matches((1080, 1920), 2, (1920, 1080)));
        assert!(texture_matches((1080, 1920), 4, (1920, 1080)));
        assert!(texture_matches((1920, 1080), 3, (1920, 1080)));
        assert!(!texture_matches((1080, 1920), 2, (1080, 1920)));
        assert!(!texture_matches((2560, 1440), 1, (1920, 1080)));
    }
    #[test]
    fn media_buffer_alignment_accepts_byte_sizes_and_driver_masks() {
        for (required, mask) in [(0, 15), (1, 15), (15, 15), (16, 15), (31, 31), (32, 31), (512, 511)] {
            assert_eq!(buffer_alignment(required), Some(mask));
        }
        assert!(buffer_alignment(u32::MAX).is_none());
    }
    #[test]
    fn rotations_and_padded_rows_keep_pixels_in_place() {
        let input = [0, 0, 10, 255, 0, 0, 20, 255, 9, 9, 9, 9, 0, 0, 30, 255, 0, 0, 40, 255, 9, 9, 9, 9];
        let red = |v: Vec<u8>| v.as_chunks::<4>().0.iter().map(|p| p[0]).collect::<Vec<_>>();
        assert_eq!(red(rgba(&input, 2, 2, 12, 1, (2, 2))), [10, 20, 30, 40]);
        assert_eq!(red(rgba(&input, 2, 2, 12, 2, (2, 2))), [30, 10, 40, 20]);
        assert_eq!(red(rgba(&input, 2, 2, 12, 4, (2, 2))), [20, 40, 10, 30]);
    }
    #[test]
    fn resampling_matches_reference_for_rotated_and_padded_patterns() {
        for (width, height) in [(3, 5), (17, 9), (8, 6)] {
            let stride = width * 4 + 8;
            let source = (0..stride * height).map(|i| ((i * 37 + i / 19) % 256) as u8).collect::<Vec<_>>();
            for rotation in 0..=4 {
                for out in [(2, 2), (6, 4), (10, 14)] {
                    let expected = reference_rgba(&source, width, height, stride, rotation, out);
                    let actual = rgba(&source, width, height, stride, rotation, out);
                    assert_eq!(actual.len(), expected.len());
                    assert!(
                        actual.iter().zip(expected).all(|(a, b)| a.abs_diff(b) <= 1),
                        "{width}x{height}, rotation {rotation}, output {out:?}"
                    );
                }
            }
        }
    }
    #[test]
    fn full_size_text_pixels_are_exact_and_frame_buffers_are_reused() {
        let mut source = (0..48).map(|i| (i * 17) as u8).collect::<Vec<_>>();
        let resizer = Resizer::new((4, 2), (4, 2), 1);
        let mut rgba = vec![0; 32];
        let mut yuv = vec![0; 12];
        let (rgba_ptr, yuv_ptr) = (rgba.as_ptr(), yuv.as_ptr());
        for _ in 0..3 {
            source[2] = source[2].wrapping_add(1);
            resizer.rgba(&source, 24, &mut rgba);
            assert_eq!(rgba, reference_rgba(&source, 4, 2, 24, 1, (4, 2)));
            nv12_into(&rgba, 4, 2, &mut yuv);
            assert_eq!(rgba_ptr, rgba.as_ptr());
            assert_eq!(yuv_ptr, yuv.as_ptr());
        }
    }
    #[test]
    fn nv12_uses_video_range_and_averages_chroma() {
        assert_eq!(nv12(&[0; 16], 2, 2), [16, 16, 16, 16, 128, 128]);
        assert_eq!(nv12(&[255; 16], 2, 2), [235, 235, 235, 235, 128, 128]);
    }
    #[test]
    fn monochrome_cursor_preserves_and_inverts_background() {
        let mut image = vec![50; 16];
        cursor(
            &mut image,
            (2, 2),
            (2, 2),
            Cursor { data: &[0xc0, 0x40], width: 2, height: 2, pitch: 1, kind: 1, x: 0, y: 0 },
        );
        assert_eq!(image[0], 50);
        assert_eq!(image[4], 205);
    }
}
