//! Bounded desktop geometry, rotation, cursor composition and video colors.
//! Kept independent of Windows so the capture math is tested on every host.

pub(crate) fn size(width: u32, height: u32, maximum: u32, original: bool) -> Option<(u32, u32)> {
    if width < 2 || height < 2 || width > 16_384 || height > 16_384 || u64::from(width) * u64::from(height) > 67_108_864
    {
        return None;
    }
    let longest = maximum.clamp(352, if original { 3840 } else { 1920 }) as f64;
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
    for y in (0..height).step_by(2) {
        for x in (0..width).step_by(2) {
            let (mut r, mut g, mut b) = (0, 0, 0);
            for dy in 0..2 {
                for dx in 0..2 {
                    let p = ((y + dy) * width + x + dx) * 4;
                    let (pr, pg, pb) = (rgba[p] as i32, rgba[p + 1] as i32, rgba[p + 2] as i32);
                    output[(y + dy) * width + x + dx] = clamp(((66 * pr + 129 * pg + 25 * pb + 128) >> 8) + 16);
                    r += pr;
                    g += pg;
                    b += pb;
                }
            }
            let p = width * height + y / 2 * width + x;
            output[p] = clamp(((-38 * (r / 4) - 74 * (g / 4) + 112 * (b / 4) + 128) >> 8) + 128);
            output[p + 1] = clamp(((112 * (r / 4) - 94 * (g / 4) - 18 * (b / 4) + 128) >> 8) + 128);
        }
    }
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

fn valid_nal(nal: &[u8]) -> bool {
    !nal.is_empty()
        && nal[0] & 0x80 == 0
        && nal[0] & 0x1f != 0
        && !nal.windows(3).any(|w| w[0] == 0 && w[1] == 0 && w[2] <= 2)
}

fn avcc_to_annex_b(data: &[u8]) -> Option<Vec<u8>> {
    let mut output = Vec::with_capacity(data.len());
    let mut offset = 0;
    while offset < data.len() {
        let len = u32::from_be_bytes(data.get(offset..offset + 4)?.try_into().ok()?) as usize;
        offset += 4;
        if len == 0 || len > data.len() - offset {
            return None;
        }
        let nal = &data[offset..offset + len];
        if !valid_nal(nal) {
            return None;
        }
        output.extend_from_slice(&[0, 0, 0, 1]);
        output.extend_from_slice(nal);
        offset += len;
    }
    (!output.is_empty()).then_some(output)
}

fn start_code_len(data: &[u8], offset: usize) -> Option<usize> {
    if data.get(offset..offset + 4) == Some(&[0, 0, 0, 1]) {
        Some(4)
    } else if data.get(offset..offset + 3) == Some(&[0, 0, 1]) {
        Some(3)
    } else {
        None
    }
}

fn valid_annex_b(data: &[u8]) -> bool {
    let Some(first_len) = start_code_len(data, 0) else {
        return false;
    };
    let mut starts = vec![(0usize, first_len)];
    let mut offset = first_len;
    while offset < data.len() {
        if let Some(len) = start_code_len(data, offset) {
            starts.push((offset, len));
            offset += len;
        } else {
            offset += 1;
        }
    }
    for (index, (start, code_len)) in starts.iter().copied().enumerate() {
        let payload = start + code_len;
        let end = starts.get(index + 1).map_or(data.len(), |(next, _)| *next);
        if !valid_nal(&data[payload..end]) {
            return false;
        }
    }
    true
}

pub(crate) fn annex_b(data: &[u8]) -> Option<Vec<u8>> {
    if let Some(converted) = avcc_to_annex_b(data) {
        return Some(converted);
    }
    valid_annex_b(data).then(|| data.to_vec())
}

pub(crate) fn has_nal(data: &[u8], kind: u8) -> bool {
    data.windows(4).any(|w| w[..3] == [0, 0, 1] && w[3] & 31 == kind)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn resolution_and_portrait_respect_pixel_budget() {
        assert_eq!(size(3840, 2160, 3840, true), Some((3840, 2160)));
        assert_eq!(size(2160, 3840, 3840, true), Some((2160, 3840)));
        assert_eq!(size(5120, 1440, 3840, true), Some((3840, 1080)));
        assert_eq!(size(1920, 1200, 1280, false), Some((1152, 720)));
        assert!(size(u32::MAX, 2, 3840, true).is_none());
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
    #[test]
    fn bitstream_conversion_rejects_partial_nals() {
        let data = [0, 0, 0, 2, 0x65, 0x88];
        let result = annex_b(&data).unwrap();
        assert!(has_nal(&result, 5));
        assert!(annex_b(&data[..5]).is_none());
        assert!(annex_b(&[0, 0, 0, 0]).is_none());
        let headers = annex_b(&[0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68]).unwrap();
        assert!(has_nal(&headers, 7));
        assert!(has_nal(&headers, 8));
        assert!(!has_nal(&result, 7));
        assert!(!has_nal(&result, 8));
    }

    #[test]
    fn bitstream_conversion_distinguishes_avcc_prefixes_from_annex_b() {
        let avcc_nal = |payload: &[u8]| -> Vec<u8> {
            let mut buf = (payload.len() as u32).to_be_bytes().to_vec();
            buf.extend_from_slice(payload);
            buf
        };

        // 1-byte AVCC NALs (0x00000001 length prefix) followed by another AVCC NAL.
        let mut one_byte_multi = avcc_nal(&[0x09]);
        one_byte_multi.extend_from_slice(&avcc_nal(&[0x65, 0x88, 0x84]));
        assert_eq!(annex_b(&one_byte_multi).unwrap(), [0, 0, 0, 1, 0x09, 0, 0, 0, 1, 0x65, 0x88, 0x84]);
        assert_eq!(annex_b(&avcc_nal(&[0x09])).unwrap(), [0, 0, 0, 1, 0x09]);

        // AVCC NAL lengths in 256..=511 start with [0, 0, 1, <lsb>], which must not
        // be mistaken for a 3-byte Annex B start code.
        for len in [256usize, 261, 300, 511] {
            let mut payload = vec![0x65];
            payload.resize(len, 0x88);
            let mut packet = avcc_nal(&payload);
            packet.extend_from_slice(&avcc_nal(&[0x41, 0x9a]));
            let converted = annex_b(&packet).unwrap();
            let mut expected = vec![0, 0, 0, 1];
            expected.extend_from_slice(&payload);
            expected.extend_from_slice(&[0, 0, 0, 1, 0x41, 0x9a]);
            assert_eq!(converted, expected, "AVCC length {len} must preserve full payload");
        }

        // Genuine 3-byte and 4-byte Annex B streams pass through unchanged.
        let three_byte_annex = [0, 0, 1, 0x67, 0x42, 0xe0, 0x1e, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80];
        assert_eq!(annex_b(&three_byte_annex).unwrap(), three_byte_annex);
        let four_byte_annex = [0, 0, 0, 1, 0x67, 0x42, 0xe0, 0x1e, 0, 0, 0, 1, 0x65, 0x88, 0x84];
        assert_eq!(annex_b(&four_byte_annex).unwrap(), four_byte_annex);

        // Malformed and truncated inputs are rejected.
        for invalid in [
            &[][..],
            &[0, 0, 1][..],
            &[0, 0, 0, 1][..],
            &[0, 0, 0, 1, 0x00][..],
            &[0, 0, 0, 1, 0x85][..],
            &[0, 0, 0, 1, 0x67, 0, 0, 0, 1][..],
            &[0, 0, 1, 0x00, 0x65, 0x88][..],
            &[0, 0, 0, 1, 0x65, 0, 0, 2, 0x88][..],
            &[0, 0, 0, 1, 0x09, 0, 0, 0, 5, 0x65][..],
        ] {
            assert!(annex_b(invalid).is_none(), "expected rejection for {invalid:?}");
        }
    }
}
