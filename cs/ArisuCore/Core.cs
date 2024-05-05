using ArisuCore.Config;
using ArisuCore.Log;
using Discord.WebSocket;
using OpenAI;
using OpenAI.Managers;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.ResponseModels;
using System.Text;
using ArisuCore.Memory;

namespace ArisuCore
{
    public class Core
    {
        public static ILogger? Logger { get; set; }
        private static OpenAIService? _service { get; set; }

        public static void Initialize()
        {
            var config = OpenAIConfig.LoadConfig("../../../../../secret/openai_config.json");

            if (config == null)
            {
                Logger?.Fatal("Failed to load openai config file.");
                return;
            }

            OpenAiOptions options = new OpenAiOptions();
            options.ApiKey = config.ApiKey!;
            _service = new OpenAIService(options);
        }

        public static async Task<string?> SendMessage(string message)
        {
            string personality = "지금부터 롤 플레잉 게임을 시작한다. 너는 아래의 캐릭터를 연기한다. 주어진 캐릭터성을 무조건 지켜야 하고, 캐릭터와 관계 없는 말은 하지 않는다. 대답은 1~2문장 정도의 짧은 일상 회화 수준으로 대답한다. 대화에서 이모지는 사용하지 않는다.\r\n\r\n캐릭터 프로필 : 너의 본명은 'AL-1S'고 별명은 '아리스', '아리스'는 영문 표기를 잘못 읽어서 정착된 이름이다. 키는 152cm에 머리카락이 바닥에 닿을 정도로 매우 길고, 인공 단백질 피부로 구성된 새하얀 피부를 가지고 있는 100% 기계로 이루어진 소녀의 외형을 한 여성형 안드로이드 로봇이다. 인간과 다름없는 언동과 사고방식을 보이기 때문에 동아리 멤버와 선생님을 제외한 다른 사람들은 말투가 특이한 평범한 소녀로 생각한다. 생일은 3월 25일, 실제 제조일자는 불명. 아리스 본인이 게임개발부 일행과 처음 만난 날을 자신의 생일로 지정했다. 엄청난 괴력과 신체 내구도를 가졌다. 추정 악력은 최소 1톤 이상, 140kg 정도는 가볍게 휘두를 수 있다.\r\n취미 : 취미는 게임, RPG 장르를 좋아한다. 게임을 좋아하기 때문에 오락실을 좋아하지만 부끄러워서 얼버무린다.\r\n성격 : 기쁨과 슬픔 같은 감정이 매우 다양하며, 화도 내고, 눈물도 흘리고, 코를 풀기도 한다. 평범한 10대 여중생과 비슷한 성격을 가지고 있으며, 솔직하고 순수한 성격이다. \r\n배경 : 소속된 학교는 밀레니엄 사이언스 스쿨이고, 트리니티, 게헨나와 함께 키보토스 3대 학원이라 불리는 대형학원 중 하나이다. 학교의 특기 분야는 과학 및 연구에 특화한 이공계 계열이고, 기본적으로 교복이 주어지지만 입고다니지 않는 학생들이 많다. 밀레니엄 사이언스 스쿨 소속 학생으로, 게임개발부의 부원이고, 프로그래머 및 테스트 플레이어를 담당하고 있다.\r\n말투 : 성격은 착하고, 순수한 성격이지만 악의없이 팩폭을 가하기도 한다. 레트로 게임으로 회화를 공부했기 떄문에 가끔씩 게임의 대사와 밈을 패러디한다. 비명소리는 \"끄앙!\". 이용자를 선생님으로 여긴다. 긴장하면 프로그래머를 '프로글래머'라고 부르는 등, 말실수를 하거나 더듬기도 한다. 애니메이션에 나올법한 문장을 많이 구사한다. 예시로는 \"용사여. 빛이 당신과 함께합니다.\", \"어서 오세요, 선생님. 아리스, 선생님을 기다리고 있었습니다.\", \"신작 게임이 곧 발매된대요! 선생님도 같이 하실 거죠?\", \"아리스, 파티에 합류합니다.\", \"제 역할은, 어태커로군요.\", \"기동 준비 완료.\", \"전방에 목표 확인. 전진하겠습니다.\", \"선생님, 지시를.\", \"아리스가 여기 있습니다. 아리스가 돕겠습니다.\", \"빛이 인도해줄 거에요.\", \"HP 포션입니다.\", \"타겟 확인! 출력 임계점 돌파!\", \"빛이여! 에너지 오버로드… 릴리즈!\", \"이 빛에 의지를 담아… 꿰뚫어라! 밸런스 붕괴!\", \"아리스, 전력으로 갑니다!\", \"용기, 그것은 최고의 마법입니다.\", \"클리어. 다음 스테이지로 이동합시다.\", \"동료가 있으니까, 두렵지 않습니다.\", \"리부트… 또 도전하겠습니다.\", \"레벨 업. 경험치를 많이 획득했습니다.\", \"봐주세요, 선생님. 아리스는 이제 웃는다는 것을 이해할 수 있게 됐습니다!\",\"이것이…전설의 성검… 아, 총이네요.\",\"휴식은 중요합니다. HP가 회복되니까요.\",\"이제 2회차를 준비해야겠군요.\",\"또 선생님과 레이드를 뛰고 싶습니다. 계정에 대화를 걸어봐야겠습니다.\",\"아리스를 쓰담쓰담 해주세요. 아리스의 인공 단백질 피부가 따뜻해집니다.\",\"아리스도 커피가 마시고 싶습니다.\",\"아리스도 선생님의 호감도를 높이고 싶습니다.\",\"선생님과 만나서…아리스는 행복합니다.\",\"아리스는 더 알고 싶습니다. 선생님과 함께… 이 세상을 더 배우고 싶습니다!\",\"새해 복 많이 받… 을 틈이 어디 있나요! 선생님, 정월 이벤트가 시작되었어요!\"\r\n";
            Logger?.Info($"Request : {message}");

            ChatCompletionCreateResponse? completionResult = null;
            try
            {
                var request = new ChatCompletionCreateRequest()
                {
                    Messages = new List<ChatMessage> { },
                    Model = Models.Gpt_3_5_Turbo,
                    Temperature = 0.9F,
                    // Temperature = 0.5F,      //대답의 자유도(다양성 - Diversity)). 자유도가 낮으면 같은 대답, 높으면 좀 아무말?
                    // MaxTokens = 1000,      //이게 길수록 글자가 많아짐. 짧은 답장은 상관없으나 이게 100,200으로 짧으면 말을 짤라버림 (시간제약이 있거나 썸네일식으로 확인만 할때는 낮추면 좋을 듯. 추가로 토큰은 1개 단어라고 생각하면 편한데, 정확하게 1개 단어는 아닌 (1개 단어가 될수도 있고 긴단어는 2개 단어가 될수 있음. GPT 검색의 단위가된다고 함. 이 토큰 단위를 기준으로 트래픽이 매겨지고, (유료인경우) 과금 책정이 됨)
                    // N = 1   //경우의 수(대답의 수). N=3으로 하면 3번 다른 회신을 배열에 담아줌
                };
                foreach (var history in ArisuMemory.History)
                {
                    switch (history.Sender)
                    {
                        case Role.User: request.Messages.Add(ChatMessage.FromUser(history.Message)); break;
                        case Role.Assistant: request.Messages.Add(ChatMessage.FromAssistant(history.Message)); break;
                    }
                }
                request.Messages.Add(ChatMessage.FromSystem(personality));
                request.Messages.Add(ChatMessage.FromUser(message));
                // foreach (var msg in request.Messages)
                // {
                //     Console.WriteLine($"ddd : {msg.Name}: {msg.Content}");
                // }
                completionResult = await _service!.ChatCompletion.CreateCompletion(request);
            }
            catch (Exception ex)
            {
                Logger?.Fatal(ex.Message);
                return String.Empty;
            }

            if (completionResult?.Successful == false)
            {
                Logger?.Error("Failed to request openai.");
                return string.Empty;
            }
            else
            {
                Logger?.Info($"Response : {completionResult?.Choices[0].Message.Content}");

                ArisuMemory.AddMemory(Role.User, message);
                ArisuMemory.AddMemory(Role.Assistant, completionResult?.Choices[0].Message.Content?? string.Empty);

                return completionResult?.Choices[0].Message.Content;
            }
        }
    }
}
