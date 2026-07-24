# 本脚本同步鸣潮全角色官方立绘，并重建带来源审计信息的美图库目录。
# 社交平台作品只有热度达到门槛才进入优先池；官方客户端立绘仅作为全角色兜底。
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$MinimumEngagement = 5000
)

$ErrorActionPreference = 'Stop'
$galleryRoot = Join-Path $ProjectRoot 'resources\gallery'
$officialRoot = Join-Path $galleryRoot 'official'
$catalogPath = Join-Path $galleryRoot 'catalog.json'
New-Item -ItemType Directory -Force -Path $officialRoot | Out-Null

$characters = [ordered]@{
    'calcharo'       = '卡卡罗'
    'changli'        = '长离'
    'encore'         = '安可'
    'jinhsi'         = '今汐'
    'jiyan'          = '忌炎'
    'lingyang'       = '凌阳'
    'rover-havoc'    = '漂泊者·湮灭'
    'rover-spectro'  = '漂泊者·衍射'
    'rover-aero'     = '漂泊者·气动'
    'rover-electro'  = '漂泊者·导电'
    'verina'         = '维里奈'
    'xiangli-yao'    = '相里要'
    'yinlin'         = '吟霖'
    'zhezhi'         = '折枝'
    'shorekeeper'    = '守岸人'
    'camellya'       = '椿'
    'carlotta'       = '珂莱塔'
    'roccia'         = '洛可可'
    'brant'          = '布兰特'
    'phoebe'         = '菲比'
    'cantarella'     = '坎特蕾拉'
    'zani'           = '赞妮'
    'ciaccona'       = '夏空'
    'cartethyia'     = '卡提希娅'
    'lupa'           = '露帕'
    'phrolova'       = '弗洛洛'
    'augusta'        = '奥古斯塔'
    'iuno'           = '尤诺'
    'qiuyuan'        = '仇远'
    'galbrena'       = '嘉贝莉娜'
    'chisa'          = '千咲'
    'mornye'         = '莫宁'
    'lynae'          = '琳奈'
    'aemeath'        = '爱弥斯'
    'luuk-herssen'   = '陆·赫斯'
    'sigrika'        = '西格莉卡'
    'denia'          = '达妮娅'
    'hiyuki'         = '绯雪'
    'lucy'           = '露西'
    'rebecca'        = '丽贝卡'
    'lucilla'        = '洛瑟菈'
    'aalto'          = '秋水'
    'baizhi'         = '白芷'
    'chixia'         = '赤霞'
    'danjin'         = '丹瑾'
    'jianxin'        = '鉴心'
    'mortefi'        = '莫特斐'
    'sanhua'         = '散华'
    'taoqi'          = '桃祈'
    'yangyang'       = '秧秧'
    'yangyang-xuanling' = '秧秧·玄翎'
    'suisui'         = '穗穗'
    'yuanwu'         = '渊武'
    'youhu'          = '釉瑚'
    'lumi'           = '灯灯'
    'buling'         = '卜灵'
}

$items = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $characters.GetEnumerator()) {
    $slug = $entry.Key
    $character = $entry.Value
    $webp = Join-Path $officialRoot ($slug + '.webp')
    $jpg = Join-Path $officialRoot ($slug + '.jpg')
    $sourceImage = "https://img.gachia.com/images/wuwa/characters/$slug.webp"
    if (-not (Test-Path $jpg)) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $sourceImage -OutFile $webp -TimeoutSec 60
            & python -c "from PIL import Image; im=Image.open(r'$webp').convert('RGB'); im.thumbnail((1600,1600)); im.save(r'$jpg', 'JPEG', quality=92, optimize=True)"
            Remove-Item -LiteralPath $webp -Force
        }
        catch {
            if (Test-Path $webp) { Remove-Item -LiteralPath $webp -Force }
            try {
                $detailUrl = "https://wuthering.gg/characters/$slug"
                $page = (Invoke-WebRequest -UseBasicParsing -Uri $detailUrl -TimeoutSec 60).Content
                $match = [regex]::Match(
                    $page,
                    '<section class="character">.*?src="([^"]*?/images/iconrolepile/[^"&]+\.png)"',
                    [System.Text.RegularExpressions.RegexOptions]::Singleline)
                if (-not $match.Success) { throw "No official role portrait was found on the fallback page." }
                $fallbackPath = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
                $fallbackImage = 'https://wuthering.gg' + $fallbackPath
                $png = Join-Path $officialRoot ($slug + '.png')
                Invoke-WebRequest -UseBasicParsing -Uri $fallbackImage -OutFile $png -TimeoutSec 60
                & python -c "from PIL import Image; im=Image.open(r'$png').convert('RGB'); im.thumbnail((1600,1600)); im.save(r'$jpg', 'JPEG', quality=92, optimize=True)"
                Remove-Item -LiteralPath $png -Force
            }
            catch {
                Write-Warning "Skipped unavailable official portrait: $character ($slug) - $($_.Exception.Message)"
                continue
            }
        }
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $jpg).Hash.ToLowerInvariant()
    $items.Add([ordered]@{
        id = "official-$slug"
        character = $character
        aliases = @($slug)
        title = "$character 官方角色立绘"
        localPath = "resources/gallery/official/$slug.jpg"
        sourceUrl = "https://wuthering.gg/characters/$slug"
        publisher = '库洛游戏客户端资源（Wuthering.gg 索引）'
        sourceKind = 'official-game-asset'
        nonAiBasis = '游戏客户端官方角色资源；非生成式 AI 作品'
        isOfficial = $true
        likes = 0
        favorites = 0
        sha256 = $hash
    })
}

$featured = @(
    @{ id='featured-xuanling-combat'; file='yangyang-combat-demo.jpg'; title='共鸣者战斗演示｜秧秧·玄翎'; bvid='BV1mtTD6rEtQ'; likes=107302; favorites=16369 },
    @{ id='featured-xuanling-nightmare'; file='yangyang-nightmare-pv.jpg'; title='秧秧·玄翎 PV｜噩梦'; bvid='BV1ZKMe61Eth'; likes=109242; favorites=23104 },
    @{ id='featured-xuanling-constellation'; file='yangyang-constellation.jpg'; title='寰宇人类注疏：群星交错｜秧秧·玄翎'; bvid='BV1L8jT69EgW'; likes=134994; favorites=15805 },
    @{ id='featured-xuanling-fourth-fall'; file='yangyang-fourth-fall.jpg'; title='过场动画｜第四次于风中坠落'; bvid='BV12DNN63EXa'; likes=42430; favorites=11474 }
)

foreach ($entry in $featured) {
    if ([Math]::Max($entry.likes, $entry.favorites) -lt $MinimumEngagement) { continue }
    $sourcePath = Join-Path $ProjectRoot ("resources\yangyang-gallery\official\" + $entry.file)
    if (-not (Test-Path $sourcePath)) { continue }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
    $items.Add([ordered]@{
        id = $entry.id
        character = '秧秧·玄翎'
        aliases = @('玄翎', '秧秧玄翎', 'xuanling', 'yangyang-xuanling')
        title = $entry.title
        localPath = "resources/yangyang-gallery/official/$($entry.file)"
        sourceUrl = "https://www.bilibili.com/video/$($entry.bvid)/"
        publisher = '鸣潮官方'
        sourceKind = 'verified-high-engagement-official'
        nonAiBasis = '鸣潮官方发布的视频封面；公开互动数据超过 5000'
        isOfficial = $true
        likes = [long]$entry.likes
        favorites = [long]$entry.favorites
        sha256 = $hash
    })
}

$catalog = [ordered]@{
    version = 1
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    minimumEngagement = $MinimumEngagement
    policy = '优先发送互动数达到门槛的非 AI/官方作品；无高热作品时仅回退游戏客户端官方角色立绘。'
    items = $items
}
$catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $catalogPath -Encoding UTF8
Write-Host "Gallery synced: $($items.Count) items / $($characters.Count + 1) character variants"




