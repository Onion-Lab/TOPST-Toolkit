# Assets 출처

2026-11-20에 받았다. 규칙과 쓰임은 [docs/design-system.md](../../../docs/design-system.md).

## Brand

| 파일 | 출처 | 비고 | SHA-256 |
|---|---|---|---|
| `Brand/topst-logo.svg` | `https://topst.ai/logo.svg` 그대로 | TOPST 및 TOPST 심볼은 Telechips Inc.의 등록 상표 | `99D2DC89C82BA6C2E84B4510467C7AD51121E0B8AB22EC4B42F945C7F89D46EA` |
| `Brand/topst-symbol.svg` | 위 파일에서 워드마크(검정 경로 5개)와 200×47 클립을 지우고 정사각 viewBox(`0 -0.84 48.6 48.6`)로 자른 것 | 점 25개·그라데이션 25개는 원본 그대로 | `B3469FCB8A940C0F7C239287A244D3DE0BDDAEF7F649648D40A80F7631D9732C` |
| `Brand/app.ico` | `topst-symbol.svg`에서 `build\gen-app-icon.py`로 만든 앱 아이콘 (2026-11-24) | 16~32 px는 "T" 단순형, 40~256 px는 원본 모양. 다시 만들 때는 스크립트를 돌린다 | — |
| `Brand/telechips-wordmark.svg` | `https://www.telechips.com` 머리글의 인라인 SVG (130×24) | 페이지에서 직렬화한 값과 SHA-256 일치 확인. `fill="currentColor"` 단색. Telechips 상표 | `6ADB51CEBE00A5B977DB117FEED0C06D1BB7F4DBCDB9D17F25B938BE2E8D364C` |

사내 공식 CI 파일이 생기면 이 파일들을 교체한다.

## Fonts (모두 SIL Open Font License 1.1, 라이선스 전문 동봉)

| 파일 | 출처 | SHA-256 |
|---|---|---|
| `Fonts/Pretendard-Light.otf` | `github.com/orioncactus/pretendard` 릴리스 v1.3.9 `Pretendard-1.3.9.zip` → `public/static/` | `51418EE96EF96E9059DB88D7DA82F24FADA1F4ACA70D5157C878EA6B460DD343` |
| `Fonts/Pretendard-Regular.otf` | 〃 | `3FFBACDE6AB8411F1D2DB54BB9B1F0B3EE2A738932033722CF0388C06AED1C93` |
| `Fonts/Pretendard-Medium.otf` | 〃 | `D39E50E4BB52B4993B6A4EEB821A171254745BD824446AF01E1F616B89FFACE0` |
| `Fonts/Pretendard-SemiBold.otf` | 〃 | `C89BC43027DC7CDE5726E96223376F8EEC09302B2FC1F8147FD5B57CFC376118` |
| `Fonts/Pretendard-Bold.otf` | 〃 | `2E91915FAB54DF71CC9598EBF608B2BDB54C6FE3C066AC61DFF0BC44FCA71CC7` |
| `Fonts/LICENSE-Pretendard.txt` | 같은 zip의 `LICENSE.txt` | — |
| `Fonts/SourceSans3-Semibold.otf` | `github.com/adobe-fonts/source-sans` 릴리스 3.052R `OTF-source-sans-3.052R.zip` → `OTF/` | `36F1CD2C344AA3109E64429BCE41A41E4B2B923BEB2DB19B8DBF9BB56F05A4F9` |
| `Fonts/SourceSans3-Bold.otf` | 〃 | `7776DDB9F3EB58683E59F28D558D8896B768C2D0C80799FB3F1C56C54DFD98C9` |
| `Fonts/LICENSE-SourceSans3.md` | 같은 저장소 태그 3.052R의 `LICENSE.md` (zip에 없어서 따로 받음) | — |
| `Fonts/D2Coding-Ver1.3.2-20180524.ttf` | `github.com/naver/d2codingfont` 릴리스 VER1.3.2 zip → `D2Coding/` | `8B1B23E5DE4DFF652FB0B938528150D2F531EDFDA281D3944618B655711ABA84` |
| `Fonts/LICENSE-D2Coding.md` | 같은 저장소(`naver/d2-coding-font`)의 `LICENSE.md` (zip에 없어서 따로 받음) | — |

받은 원본 압축 파일은 저장소 밖 `E:\TOPST\tc-nn-toolkit\win-verify\downloads\brand\`에 있다.

## WPF에서 확인한 패밀리 이름

| 패밀리 | 굵기 |
|---|---|
| `Pretendard` | Light, Normal, Medium, SemiBold, Bold (한 패밀리로 묶임) |
| `Source Sans 3` | SemiBold, Bold (한글 글리프 없음) |
| `D2Coding` | Normal (한글 포함) |

참조 예: `FontFamily="pack://application:,,,/Assets/Fonts/#Pretendard"` + `FontWeight="SemiBold"`.
