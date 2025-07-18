﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Relation;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Video;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService;

/// <summary>
/// 视频
/// </summary>
public class VideoDomainService(
    ILogger<VideoDomainService> logger,
    IOptionsMonitor<DailyTaskOptions> dailyTaskOptions,
    IRelationApi relationApi,
    IVideoApi videoApi,
    IVideoWithoutCookieApi videoWithoutCookieApi
) : IVideoDomainService
{
    private readonly DailyTaskOptions _dailyTaskOptions = dailyTaskOptions.CurrentValue;
    private readonly Dictionary<string, int> _expDic = Config.Constants.ExpDic;

    /// <summary>
    /// 获取视频详情
    /// </summary>
    /// <param name="aid"></param>
    /// <returns></returns>
    public async Task<VideoDetail> GetVideoDetail(string aid)
    {
        var re = await videoWithoutCookieApi.GetVideoDetail(aid);
        return re.Data!;
    }

    /// <summary>
    /// 从排行榜获取随机视频
    /// </summary>
    /// <returns></returns>
    public async Task<RankingInfo> GetRandomVideoOfRanking()
    {
        var apiResponse = await videoWithoutCookieApi.GetRegionRankingVideosV2();
        logger.LogDebug("获取排行榜成功");
        var data = apiResponse.Data.List[new Random().Next(apiResponse.Data.List.Count)];
        return data;
    }

    public async Task<UpVideoInfo?> GetRandomVideoOfUp(long upId, int total, BiliCookie ck)
    {
        if (total <= 0)
            return null;

        var req = new SearchVideosByUpIdDto()
        {
            mid = upId,
            ps = 1,
            pn = new Random().Next(1, total + 1),
        };

        BiliApiResponse<SearchUpVideosResponse> re = await videoApi.SearchVideosByUpId(
            req,
            ck.ToString()
        );

        if (re.Code != 0)
        {
            throw new Exception(re.Message);
        }

        return re.Data?.List?.Vlist.FirstOrDefault();
    }

    /// <summary>
    /// 获取UP主的视频总数量
    /// </summary>
    /// <param name="upId"></param>
    /// <returns></returns>
    public async Task<int> GetVideoCountOfUp(long upId, BiliCookie ck)
    {
        var req = new SearchVideosByUpIdDto() { mid = upId };

        BiliApiResponse<SearchUpVideosResponse> re = await videoApi.SearchVideosByUpId(
            req,
            ck.ToString()
        );
        if (re.Code != 0)
        {
            throw new Exception(re.Message);
        }

        return re.Data!.Page.Count;
    }

    public async Task WatchAndShareVideo(DailyTaskInfo dailyTaskStatus, BiliCookie ck)
    {
        VideoInfoDto? targetVideo = null;

        //至少有一项未完成，获取视频
        if (!dailyTaskStatus.Watch || !dailyTaskStatus.Share)
        {
            targetVideo = await GetRandomVideoForWatchAndShare(ck);
            logger.LogInformation("【随机视频】{title}", targetVideo.Title);
        }

        bool watched = false;
        //观看
        if (!dailyTaskStatus.Watch && _dailyTaskOptions.IsWatchVideo)
        {
            await WatchVideo(targetVideo!, ck);
            watched = true;
        }
        else
            logger.LogInformation("今天已经观看过了，不需要再看啦");

        //分享
        if (!dailyTaskStatus.Share && _dailyTaskOptions.IsShareVideo)
        {
            //如果没有打开观看过，则分享前先打开视频
            if (!watched)
            {
                try
                {
                    await OpenVideo(targetVideo!, ck);
                }
                catch (Exception e)
                {
                    //ignore
                    logger.LogError("打开视频异常：{msg}", e.Message);
                }
            }
            await ShareVideo(targetVideo!, ck);
        }
        else
            logger.LogInformation("今天已经分享过了，不用再分享啦");
    }

    /// <summary>
    /// 观看视频
    /// </summary>
    public async Task WatchVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        //开始上报一次
        await OpenVideo(videoInfo, ck);

        //结束上报一次
        videoInfo.Duration = videoInfo.Duration ?? 15;
        int max = videoInfo.Duration < 15 ? videoInfo.Duration.Value : 15;
        int playedTime = new Random().Next(1, max);

        var request = new UploadVideoHeartbeatRequest
        {
            Aid = long.Parse(videoInfo.Aid),
            Bvid = videoInfo.Bvid,
            Cid = videoInfo.Cid,
            Mid = long.Parse(ck.UserId),
            Csrf = ck.BiliJct,

            Played_time = playedTime,
            Realtime = playedTime,
            Real_played_time = playedTime,
        };
        BiliApiResponse apiResponse = await videoApi.UploadVideoHeartbeat(request, ck.ToString());

        if (apiResponse.Code == 0)
        {
            _expDic.TryGetValue("每日观看视频", out int exp);
            logger.LogInformation(
                "视频播放成功，已观看到第{playedTime}秒，经验+{exp} √",
                playedTime,
                exp
            );
        }
        else
        {
            logger.LogError("视频播放失败，原因：{msg}", apiResponse.Message);
        }
    }

    /// <summary>
    /// 分享视频
    /// </summary>
    /// <param name="videoInfo">视频</param>
    public async Task ShareVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        var request = new ShareVideoRequest(long.Parse(videoInfo.Aid), ck.BiliJct);
        BiliApiResponse apiResponse = await videoApi.ShareVideo(request, ck.ToString());

        if (apiResponse.Code == 0)
        {
            _expDic.TryGetValue("每日观看视频", out int exp);
            logger.LogInformation("视频分享成功，经验+{exp} √", exp);
        }
        else
        {
            logger.LogError("视频分享失败，原因: {msg}", apiResponse.Message);
        }
    }

    /// <summary>
    /// 模拟打开视频播放（初始上报一次进度）
    /// </summary>
    /// <param name="videoInfo"></param>
    /// <returns></returns>
    private async Task<bool> OpenVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        var request = new UploadVideoHeartbeatRequest
        {
            Aid = long.Parse(videoInfo.Aid),
            Bvid = videoInfo.Bvid,
            Cid = videoInfo.Cid,

            Mid = long.Parse(ck.UserId),
            Csrf = ck.BiliJct,
        };

        //开始上报一次
        BiliApiResponse apiResponse = await videoApi.UploadVideoHeartbeat(request, ck.ToString());

        if (apiResponse.Code == 0)
        {
            logger.LogDebug("打开视频成功");
            return true;
        }
        else
        {
            logger.LogError("视频打开失败，原因：{msg}", apiResponse.Message);
            return false;
        }
    }

    #region private
    /// <summary>
    /// 获取一个视频用来观看并分享
    /// </summary>
    /// <returns></returns>
    private async Task<VideoInfoDto> GetRandomVideoForWatchAndShare(BiliCookie ck)
    {
        //先从配置的或关注的up中取
        var video = await GetRandomVideoOfFollowingUps(ck);
        if (video != null)
            return video;

        //然后从排行榜中取
        var t = await GetRandomVideoOfRanking();
        return new VideoInfoDto
        {
            Aid = t.Aid.ToString(),
            Bvid = t.Bvid,
            Cid = t.Cid,
            Copyright = t.Copyright,
            Duration = t.Duration,
            Title = t.Title,
        };
    }

    private async Task<VideoInfoDto?> GetRandomVideoOfFollowingUps(BiliCookie ck)
    {
        //配置的UpId
        int configUpsCount = _dailyTaskOptions.SupportUpIdList.Count;
        if (configUpsCount > 0)
        {
            var video = await GetRandomVideoOfUps(_dailyTaskOptions.SupportUpIdList, ck);
            if (video != null)
                return video;
        }

        //关注列表
        var request = new GetFollowingsRequest(long.Parse(ck.UserId));
        BiliApiResponse<GetFollowingsResponse> result = await relationApi.GetFollowings(
            request,
            ck.ToString()
        );
        if (result.Data.Total > 0)
        {
            var video = await GetRandomVideoOfUps(result.Data.List.Select(x => x.Mid).ToList(), ck);
            if (video != null)
                return video;
        }

        return null;
    }

    /// <summary>
    /// 从up集合中获取一个随机视频
    /// </summary>
    /// <param name="upIds"></param>
    /// <returns></returns>
    private async Task<VideoInfoDto?> GetRandomVideoOfUps(List<long> upIds, BiliCookie ck)
    {
        long upId = upIds[new Random().Next(0, upIds.Count)];

        if (upId == 0 || upId == long.MinValue)
            return null;

        int count = await GetVideoCountOfUp(upId, ck);

        if (count > 0)
        {
            var video = await GetRandomVideoOfUp(upId, count, ck);
            if (video == null)
                return null;
            return new VideoInfoDto
            {
                Aid = video.Aid.ToString(),
                Bvid = video.Bvid,
                //Cid=,
                //Copyright=
                Title = video.Title,
                Duration = video.Duration,
            };
        }

        return null;
    }
    #endregion private
}
