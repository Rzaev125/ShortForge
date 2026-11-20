using ShortForge.Core;
using System.Text.Json;
using Xunit;

namespace ShortForge.Core.Tests;

public class ContractTests
{
    [Theory]
    [InlineData("https://other.local/index.html")]
    [InlineData("http://app.shortforge.local/index.html")]
    [InlineData("https://app.shortforge.local:3000/index.html")]
    [InlineData("https://app.shortforge.local/assets/foo.html")]
    public void OnlyPackagedDocumentCanSendCommands(string source)
    {
        Assert.False(BridgeRequest.IsPage(source));
        Assert.True(BridgeRequest.IsPage(BridgeRequest.Page));
    }

    [Theory]
    [InlineData("projects.open", "{}")]
    [InlineData("media.rebuildProxy", "{\"projectId\":\"58c36f3c-c3a1-4765-b787-d46c57cbccd0\"}")]
    [InlineData("media.rebuildProxy", "{\"projectId\":\"58c36f3c-c3a1-4765-b787-d46c57cbccd0\",\"proxyId\":\"old.mp4\"}")]
    [InlineData("media.pick", "{\"path\":\"example.mp4\"}")]
    [InlineData("media.createProxy", "{\"projectId\":\"58c36f3c-c3a1-4765-b787-d46c57cbccd0\",\"audioTrack\":-1}")]
    [InlineData("media.createProxy", "{\"projectId\":\"58c36f3c-c3a1-4765-b787-d46c57cbccd0\",\"audioTrack\":0.5}")]
    [InlineData("projects.open", "{\"projectId\":\"58c36f3c-c3a1-4765-b787-d46c57cbccd0\",\"audioTrack\":1}")]
    [InlineData("projects.open", "{\"projectId\":4}")]
    [InlineData("projects.list", "null")]
    [InlineData("jobs.cancel", "{}")]
    [InlineData("settings.useAutomaticTools", "{\"path\":\"C:/other.json\"}")]
    [InlineData("shell.run", "{}")]
    public void RejectsUnknownOrMalformedRequests(string type, string payload)
    {
        using var body = JsonDocument.Parse(payload);
        var json = JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid(), type, payload = body.RootElement });
        Assert.Throws<UserError>(() => BridgeRequest.Parse(json));
    }

    [Fact]
    public void BatchRequestsAreBoundToProjectAndLocalPicker()
    {
        var projectId = Guid.NewGuid();
        var clipIds = new[] { Guid.NewGuid() };
        BridgeRequest Request(object payload) => BridgeRequest.Parse(JsonSerializer.Serialize(new
        { version = 1, id = Guid.NewGuid(), type = "exports.start", payload }));
        var valid = new { projectId, clipIds, selectionId = (Guid?)null, candidateStarts = Array.Empty<int>(), fps = 60, normalizeAudio = true,
            includeSrt = true, includeManifest = false, preferNvenc = false };
        Assert.Equal(projectId, Request(valid).ProjectId);
        Assert.Throws<UserError>(() => Request(valid with { fps = 59 }));
        Assert.Throws<UserError>(() => Request(valid with { clipIds = Array.Empty<Guid>() }));
        Assert.Throws<UserError>(() => Request(valid with { clipIds = new[] { clipIds[0], clipIds[0] } }));
        Assert.Throws<UserError>(() => Request(valid with { projectId = Guid.Empty }));
        Assert.Throws<UserError>(() => Request(new { projectId, clipIds, folder = "C:/arbitrary" }));
        Assert.Throws<UserError>(() => Request(valid with { candidateStarts = new[] { 1 } }));
        Assert.Throws<UserError>(() => Request(valid with { selectionId = Guid.NewGuid() }));
        Assert.Equal(projectId, Request(valid with { clipIds = Array.Empty<Guid>(), selectionId = Guid.NewGuid(), candidateStarts = new[] { 5 } }).ProjectId);
    }

    [Fact]
    public void CaptionCommandsValidateTimingStyleAndRevision()
    {
        var projectId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        BridgeRequest Request(string type, object payload) => BridgeRequest.Parse(JsonSerializer.Serialize(new
        { version=1, id=Guid.NewGuid(), type, payload }, JsonSerializerOptions.Web));
        BridgeRequest Save(CaptionStyle style, object cue) => Request("editor.save", new
        {
            projectId, clipId, revision=4, edit=new EditDecision("Клип",1000,5000,"crop",.2,.5,style), cue
        });
        var analysis = new { projectId, clipId, revision=2, intervalMs=500 };
        Assert.Equal(500, Request("editor.autoFrame", analysis).IntervalMs);
        Assert.Throws<UserError>(() => Request("editor.autoFrame", analysis with { revision=0 }));
        Assert.Throws<UserError>(() => Request("editor.autoFrame", analysis with { intervalMs=0 }));
        Assert.Throws<UserError>(() => Request("editor.autoFrame", analysis with { intervalMs=2001 }));
        Assert.Throws<UserError>(() => Request("editor.autoFrame", new {projectId,clipId,revision=2,intervalMs=500,path="input"}));
        Assert.Throws<UserError>(() => Request("editor.save", new {projectId, clipId, revision=2, cue=(object?)null,
            edit=new {title="Кадр",startUs=0,endUs=100,frame="auto",x=.5,y=.5,trajectory=new {points=Array.Empty<object>()}}}));
        var timed = new { index=0,text="Окно",sourceStartUs=800L,sourceEndUs=5200L };
        Assert.Equal(800L, Save(CaptionStyle.Neutral,timed).Cue?.SourceStartUs);
        Assert.Throws<UserError>(()=>Save(CaptionStyle.Neutral with { Color="#FFEEDD,Arial" },timed));
        Assert.Throws<UserError>(()=>Save(CaptionStyle.Neutral with { Preset="word" },timed));
        Assert.Throws<UserError>(()=>Save(CaptionStyle.Neutral,new {index=0,text="А",sourceStartUs=500}));
        Assert.Throws<UserError>(()=>Request("editor.preview",new {projectId,clipId,revision=2,offsetUs=-1}));
        Assert.Throws<UserError>(()=>Request("editor.srt",new {projectId,clipId,revision=0}));
        Assert.Throws<UserError>(()=>Request("editor.srt",new {projectId,clipId,revision=1,path="output.srt"}));
        Assert.Equal(2000L,Request("editor.preview",new {projectId,clipId,revision=1,offsetUs=2000}).OffsetUs);
    }

    [Fact]
    public void ClipTimeIntersectsHalfOpenOriginalInterval()
    {
        var edit = new EditDecision("Тест", 100, 300, "fit", 0.5, 0.5);
        Assert.Equal((0L,200L),edit.ClipTime(0,400));
        Assert.Equal((120L,200L),edit.ClipTime(220,305));
        Assert.Null(edit.ClipTime(0,100));
        Assert.Null(edit.ClipTime(300,330));
    }

    [Fact]
    public void TranscriptionBindsOnlyValidatedLanguageAndTrack()
    {
        var projectId = Guid.NewGuid();
        BridgeRequest Parse(string language, int audioTrack) => BridgeRequest.Parse(JsonSerializer.Serialize(new
        {
            version = 1, id = Guid.NewGuid(), type = "transcript.start", payload = new { projectId, language, audioTrack }
        }));
        Assert.Equal(projectId, Parse("ru", 1).ProjectId);
        Assert.Throws<UserError>(() => Parse("--model", 0));
        Assert.Throws<UserError>(() => Parse("auto", -1));
    }

    [Fact]
    public void ClipSearchRoundTripsOnlyTheFourPublicSettings()
    {
        var settings = new ClipSearch("semantic",15,30,3);
        var payload = JsonSerializer.SerializeToNode(settings, JsonSerializerOptions.Web)!.AsObject();
        payload["projectId"] = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(new { version=1, id=Guid.NewGuid(), type="clips.find", payload });
        Assert.Equal(settings,BridgeRequest.Parse(json).Search);
        Assert.Throws<UserError>(() => BridgeRequest.Parse(json.Replace("semantic","cloud")));
        Assert.Throws<UserError>(() => BridgeRequest.Parse(json.Replace("30","0",StringComparison.Ordinal)));
    }

    [Fact]
    public void OpensOnlyTheRequestedId()
    {
        var projectId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid(), type = "projects.open", payload = new { projectId } });
        Assert.Equal(projectId, BridgeRequest.Parse(json).ProjectId);
    }

    [Theory]
    [InlineData(null, 0, 10)]
    [InlineData("bytes=0-0", 0, 1)]
    [InlineData("bytes=6-", 6, 4)]
    [InlineData("bytes=-3", 7, 3)]
    [InlineData("bytes=2-99", 2, 8)]
    public void ServesExactRange(string? header, long start, long count) =>
        Assert.Equal(new ByteRange(start, count), ByteRange.Resolve(header, 10));

    [Theory]
    [InlineData("bytes=10-20")]
    [InlineData("bytes=5-4")]
    [InlineData("bytes=0-1,8-9")]
    [InlineData("bytes=-0")]
    public void RejectsInvalidRanges(string header) => Assert.Null(ByteRange.Resolve(header, 10));
}
