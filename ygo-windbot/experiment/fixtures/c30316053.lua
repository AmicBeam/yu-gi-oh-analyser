-- Heretic Eradicator. Fixture script for the published card text.
local s,id=GetID()
function s.initial_effect(c)
    local e=Effect.CreateEffect(c)
    e:SetDescription(aux.Stringid(id,0))
    e:SetCategory(CATEGORY_DISABLE_SUMMON+CATEGORY_DESTROY+CATEGORY_SPECIAL_SUMMON)
    e:SetType(EFFECT_TYPE_QUICK_O)
    e:SetCode(EVENT_SPSUMMON)
    e:SetRange(LOCATION_HAND)
    e:SetCountLimit(1,id)
    e:SetCondition(s.condition)
    e:SetTarget(s.target)
    e:SetOperation(s.operation)
    c:RegisterEffect(e)
end

function s.unsummonable(c)
    return c:IsType(TYPE_MONSTER) and not c:IsSummonableCard()
        and (c:IsLocation(LOCATION_GRAVE) or c:IsFaceup())
end

function s.condition(e,tp,eg,ep,ev,re,r,rp)
    if Duel.GetCurrentChain()~=0 or #eg~=1 or not eg:GetFirst():IsControler(1-tp) then return false end
    return Duel.IsExistingMatchingCard(s.unsummonable,tp,0,LOCATION_MZONE+LOCATION_GRAVE+LOCATION_REMOVED,4,nil)
end

function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
    local c=e:GetHandler()
    if chk==0 then
        return Duel.GetLocationCount(tp,LOCATION_MZONE)>0 and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
    end
    if Duel.GetFieldGroupCount(tp,LOCATION_MZONE,0)==0 then
        e:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_CANNOT_NEGATE)
    else
        e:SetProperty(0)
    end
    Duel.SetOperationInfo(0,CATEGORY_DISABLE_SUMMON,eg,1,0,0)
    Duel.SetOperationInfo(0,CATEGORY_DESTROY,eg,1,0,0)
    Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,c,1,0,0)
end

function s.operation(e,tp,eg,ep,ev,re,r,rp)
    local c=e:GetHandler()
    Duel.NegateSummon(eg)
    if Duel.Destroy(eg,REASON_EFFECT)>0 and c:IsRelateToEffect(e) then
        Duel.SpecialSummon(c,0,tp,tp,false,false,POS_FACEUP)
    end
end
