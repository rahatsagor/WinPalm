//! H.264 NAL framing and sequence headers shared by capture and transport.

#[cfg(any(windows, test))]
fn valid_nal(nal: &[u8]) -> bool {
    !nal.is_empty()
        && nal[0] & 0x80 == 0
        && nal[0] & 0x1f != 0
        && !nal.windows(3).any(|w| w[0] == 0 && w[1] == 0 && w[2] <= 2)
}

#[cfg(any(windows, test))]
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

fn start_codes(data: &[u8]) -> impl Iterator<Item = usize> + '_ {
    data.windows(3).enumerate().filter_map(|(i, bytes)| (bytes == [0, 0, 1]).then_some(i))
}

fn trim_nal_padding(nal: &[u8]) -> &[u8] {
    let end = nal.iter().rposition(|&byte| byte != 0).map_or(0, |i| i + 1);
    &nal[..end]
}

#[cfg(any(windows, test))]
pub(crate) fn annex_b(data: &[u8]) -> Option<Vec<u8>> {
    if let Some(converted) = avcc_to_annex_b(data) {
        return Some(converted);
    }
    let mut starts = start_codes(data);
    let first = starts.next()?;
    if data[..first].iter().any(|&byte| byte != 0) {
        return None;
    }
    let mut payload = first + 3;
    for end in starts.chain(std::iter::once(data.len())) {
        if !valid_nal(trim_nal_padding(&data[payload..end])) {
            return None;
        }
        payload = end + 3;
    }
    Some(data.to_vec())
}

#[cfg(any(windows, test))]
pub(crate) fn has_nal(data: &[u8], kind: u8) -> bool {
    start_codes(data).any(|i| data.get(i + 3).is_some_and(|header| header & 31 == kind))
}

pub(crate) fn h264_sequence_profiles(data: &[u8]) -> impl Iterator<Item = Option<u32>> + '_ {
    let mut starts = start_codes(data).peekable();
    std::iter::from_fn(move || {
        while let Some(start) = starts.next() {
            let end = starts.peek().copied().unwrap_or(data.len());
            let nal = trim_nal_padding(&data[start + 3..end]);
            if nal.first().is_some_and(|header| header & 31 == 7) {
                return Some(nal.get(1..4).map(|bytes| u32::from_be_bytes([0, bytes[0], bytes[1], bytes[2]])));
            }
        }
        None
    })
}

#[cfg(test)]
mod tests {
    use super::*;
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

    #[test]
    fn annex_b_accepts_zero_padding_without_weakening_nal_boundary_checks() {
        let padded = [0, 0, 0, 0, 1, 0x67, 0x42, 0xc0, 31, 0x80, 0, 0, 0, 0, 0, 1, 0x68, 0x80, 0, 0, 0, 0];
        assert_eq!(annex_b(&padded).as_deref(), Some(padded.as_slice()));
        assert_eq!(h264_sequence_profiles(&padded).collect::<Vec<_>>(), [Some(0x42c01f)]);
        assert!(has_nal(&padded, 8));
        assert!(annex_b(&[0, 0, 0, 0, 1, 0, 0, 0, 1, 0x67]).is_none());
        assert_eq!(h264_sequence_profiles(&[0, 0, 1, 0x67, 0, 0, 0, 1, 0x68]).next(), Some(None));
    }
}
